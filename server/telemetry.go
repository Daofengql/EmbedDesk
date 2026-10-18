// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bufio"
	"bytes"
	"errors"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// Rates count encrypted data-body bytes; WebSocket/JSON envelope and transport headers are excluded.
type TrafficMetrics struct {
	ReceivedBitsPerSecond float64 `json:"receivedBitsPerSecond"`
	SentBitsPerSecond     float64 `json:"sentBitsPerSecond"`
	TotalBitsPerSecond    float64 `json:"totalBitsPerSecond"`
	ReceivedBytes         uint64  `json:"receivedBytes"`
	SentBytes             uint64  `json:"sentBytes"`
}
type ServerMetrics struct {
	SampledAt        time.Time      `json:"sampledAt"`
	CPUPercent       *float64       `json:"cpuPercent"`
	MemoryTotalBytes *uint64        `json:"memoryTotalBytes"`
	MemoryUsedBytes  *uint64        `json:"memoryUsedBytes"`
	LoadAverage      []float64      `json:"loadAverage"`
	UptimeSeconds    *int64         `json:"uptimeSeconds"`
	Goroutines       int            `json:"goroutines"`
	HeapBytes        uint64         `json:"heapBytes"`
	Traffic          TrafficMetrics `json:"traffic"`
}
type cpuTicks struct{ total, idle uint64 }
type telemetrySampler struct {
	last           time.Time
	received, sent uint64
	cpu            *cpuTicks
}

func (r *Relay) sampleTelemetry(s *telemetrySampler) {
	now := time.Now()
	received, sent := r.receivedBytes.Load(), r.sentBytes.Load()
	m := ServerMetrics{SampledAt: now.UTC(), Goroutines: runtime.NumGoroutine(), Traffic: TrafficMetrics{ReceivedBytes: received, SentBytes: sent}}
	if elapsed := now.Sub(s.last).Seconds(); !s.last.IsZero() && elapsed > 0 {
		m.Traffic.ReceivedBitsPerSecond = float64(received-s.received) * 8 / elapsed
		m.Traffic.SentBitsPerSecond = float64(sent-s.sent) * 8 / elapsed
		m.Traffic.TotalBitsPerSecond = m.Traffic.ReceivedBitsPerSecond + m.Traffic.SentBitsPerSecond
	}
	s.last, s.received, s.sent = now, received, sent
	var memory runtime.MemStats
	runtime.ReadMemStats(&memory)
	m.HeapBytes = memory.HeapAlloc
	if runtime.GOOS == "linux" {
		if raw, err := os.ReadFile("/proc/stat"); err == nil {
			if current, err := parseCPUTicks(raw); err == nil {
				if s.cpu != nil && current.total > s.cpu.total && current.idle >= s.cpu.idle {
					total, idle := current.total-s.cpu.total, current.idle-s.cpu.idle
					if idle <= total {
						percent := float64(total-idle) * 100 / float64(total)
						m.CPUPercent = &percent
					}
				}
				s.cpu = &current
			}
		}
		if raw, err := os.ReadFile("/proc/meminfo"); err == nil {
			if total, used, err := parseHostMemory(raw); err == nil {
				m.MemoryTotalBytes = &total
				m.MemoryUsedBytes = &used
			}
		}
		if raw, err := os.ReadFile("/proc/loadavg"); err == nil {
			fields := strings.Fields(string(raw))
			if len(fields) >= 3 {
				values := make([]float64, 3)
				valid := true
				for i := range values {
					value, err := strconv.ParseFloat(fields[i], 64)
					if err != nil || value < 0 {
						valid = false
						break
					}
					values[i] = value
				}
				if valid {
					m.LoadAverage = values
				}
			}
		}
		if raw, err := os.ReadFile("/proc/uptime"); err == nil {
			if fields := strings.Fields(string(raw)); len(fields) > 0 {
				if value, err := strconv.ParseFloat(fields[0], 64); err == nil && value >= 0 {
					seconds := int64(value)
					m.UptimeSeconds = &seconds
				}
			}
		}
	}
	r.metrics.Store(&m)
}
func parseCPUTicks(raw []byte) (cpuTicks, error) {
	line, _, _ := bytes.Cut(raw, []byte{'\n'})
	fields := strings.Fields(string(line))
	if len(fields) < 9 || fields[0] != "cpu" {
		return cpuTicks{}, errors.New("invalid CPU sample")
	}
	var result cpuTicks
	// guest and guest_nice are already included in user/nice, so count only the first eight fields.
	for i := 1; i <= 8; i++ {
		value, err := strconv.ParseUint(fields[i], 10, 64)
		if err != nil {
			return cpuTicks{}, err
		}
		result.total += value
		if i == 4 || i == 5 {
			result.idle += value
		}
	}
	return result, nil
}
func parseHostMemory(raw []byte) (uint64, uint64, error) {
	values := make(map[string]uint64)
	scanner := bufio.NewScanner(bytes.NewReader(raw))
	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) != 3 || fields[2] != "kB" {
			continue
		}
		value, err := strconv.ParseUint(fields[1], 10, 64)
		if err != nil {
			return 0, 0, err
		}
		values[strings.TrimSuffix(fields[0], ":")] = value * 1024
	}
	if scanner.Err() != nil {
		return 0, 0, scanner.Err()
	}
	total := values["MemTotal"]
	available, exists := values["MemAvailable"]
	if total == 0 || !exists || available > total {
		return 0, 0, errors.New("host memory unavailable")
	}
	return total, total - available, nil
}
