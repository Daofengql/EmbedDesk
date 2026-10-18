// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/json"
	"runtime"
	"strings"
	"testing"
	"time"
)

func TestLinuxMetricParsing(t *testing.T) {
	cpu, err := parseCPUTicks([]byte("cpu 10 2 3 70 5 2 3 5 6 1\ncpu0 1 1 1 1 1 1 1 1\n"))
	if err != nil || cpu.total != 100 || cpu.idle != 75 {
		t.Fatal("CPU guest ticks double counted or idle incorrect")
	}
	for _, raw := range []string{"cpu 1", "cpu a 0 0 0 0 0 0 0", "cpu0 0 0 0 0 0 0 0 0"} {
		if _, err := parseCPUTicks([]byte(raw)); err == nil {
			t.Fatal("invalid CPU input accepted")
		}
	}
	total, used, err := parseHostMemory([]byte("MemTotal: 1024 kB\nMemAvailable: 256 kB\nMemFree: 32 kB\n"))
	if err != nil || total != 1048576 || used != 786432 {
		t.Fatal("memory must use available memory including reclaimable cache")
	}
	if _, _, err := parseHostMemory([]byte("MemTotal: 1024 kB\n")); err == nil {
		t.Fatal("missing available memory reported as fully used")
	}
}

func TestTelemetryForwardedTrafficIdleAndAdminScope(t *testing.T) {
	r, s, admin := consoleFixture(t)
	createTestUser(admin, "metricuser", "user")
	user := newConsoleClient(t, s.URL)
	user.login("metricuser")
	if _, exists := user.request("GET", "/overview", nil, 200)["server"]; exists {
		t.Fatal("host metrics exposed to ordinary user")
	}
	ac, bc := randomCredential(), randomCredential()
	aid, bid := registerDevice(t, s.URL, ac, testAdmission), registerDevice(t, s.URL, bc, testAdmission)
	a, b := socketDevice(t, s.URL, aid, ac), socketDevice(t, s.URL, bid, bc)
	sid := handshake(t, a, b, bid)
	payload := json.RawMessage(`"` + strings.Repeat("Q", 16000) + `"`)
	for i := 0; i < 6; i++ {
		if err := a.WriteJSON(Message{Type: "data", Session: sid, Body: payload}); err != nil {
			t.Fatal(err)
		}
		if readMessage(t, b).Type != "data" {
			t.Fatal("traffic failed")
		}
		time.Sleep(200 * time.Millisecond)
	}
	want := uint64(len(payload) * 6)
	deadline := time.Now().Add(3 * time.Second)
	var active *ServerMetrics
	for time.Now().Before(deadline) {
		m := r.metrics.Load()
		if m.Traffic.ReceivedBytes == want && m.Traffic.SentBytes == want && m.Traffic.TotalBitsPerSecond > 0 {
			active = m
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if active == nil {
		t.Fatalf("active traffic sample missing: %+v", r.metrics.Load().Traffic)
	}
	if active.Traffic.TotalBitsPerSecond != active.Traffic.ReceivedBitsPerSecond+active.Traffic.SentBitsPerSecond {
		t.Fatal("aggregate rate mismatch")
	}
	if runtime.GOOS == "linux" && (active.CPUPercent == nil || *active.CPUPercent < 0 || *active.CPUPercent > 100 || active.MemoryTotalBytes == nil || active.MemoryUsedBytes == nil || len(active.LoadAverage) != 3 || active.UptimeSeconds == nil) {
		t.Fatal("native Linux server metrics unavailable")
	}
	deadline = time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		m := r.metrics.Load()
		if m.SampledAt.After(active.SampledAt) && m.Traffic.TotalBitsPerSecond == 0 && m.Traffic.SentBytes == want {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("idle traffic rate did not return to zero while cumulative totals persisted")
}
