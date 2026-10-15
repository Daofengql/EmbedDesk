// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func registerDevice(t *testing.T, server, credential, admission string) string {
	t.Helper()
	body, _ := json.Marshal(map[string]string{"credential": credential})
	req, _ := http.NewRequest("POST", server+"/v1/register", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+admission)
	response, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		t.Fatalf("registration status %d", response.StatusCode)
	}
	var result struct {
		ID   string `json:"id"`
		Bits int    `json:"bitsPerSecond"`
	}
	if json.NewDecoder(response.Body).Decode(&result) != nil || len(result.ID) != 12 || result.Bits != 1000000 {
		t.Fatal("registration response")
	}
	return result.ID
}
func randomCredential() string {
	b := make([]byte, 32)
	_, _ = rand.Read(b)
	return base64.StdEncoding.EncodeToString(b)
}
func socketDevice(t *testing.T, server, id, credential string) *websocket.Conn {
	t.Helper()
	header := http.Header{"Authorization": []string{"Bearer " + credential}, "X-Device-ID": []string{id}}
	conn, _, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(server, "http")+"/v1/channel", header)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	if readMessage(t, conn).Type != "online" {
		t.Fatal("online acknowledgement")
	}
	return conn
}
func readMessage(t *testing.T, conn *websocket.Conn) Message {
	t.Helper()
	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	var m Message
	if err := conn.ReadJSON(&m); err != nil {
		t.Fatal(err)
	}
	return m
}
func TestRegisterPersistenceAndAdmission(t *testing.T) {
	path := filepath.Join(t.TempDir(), "relay.sqlite")
	r, err := newRelay(path, "test-admission")
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(r.router())
	credential := randomCredential()
	id := registerDevice(t, s.URL, credential, "test-admission")
	if registerDevice(t, s.URL, credential, "test-admission") != id {
		t.Fatal("unstable identity")
	}
	req, _ := http.NewRequest("POST", s.URL+"/v1/register", strings.NewReader(`{"credential":"bad"}`))
	resp, _ := http.DefaultClient.Do(req)
	if resp.StatusCode != 401 {
		t.Fatal("admission missing")
	}
	resp.Body.Close()
	s.Close()
	r.close()
	r, err = newRelay(path, "test-admission")
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	s = httptest.NewServer(r.router())
	defer s.Close()
	if registerDevice(t, s.URL, credential, "test-admission") != id {
		t.Fatal("identity after database reopen")
	}
}
func TestRoutingAuthorizationAndDisconnect(t *testing.T) {
	r, err := newRelay(":memory:", "")
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	s := httptest.NewServer(r.router())
	defer s.Close()
	ac, bc, cc := randomCredential(), randomCredential(), randomCredential()
	a := socketDevice(t, s.URL, registerDevice(t, s.URL, ac, ""), ac)
	bid := registerDevice(t, s.URL, bc, "")
	b := socketDevice(t, s.URL, bid, bc)
	c := socketDevice(t, s.URL, registerDevice(t, s.URL, cc, ""), cc)
	hello := json.RawMessage(`{"requestId":"01234567890123456789012345678901"}`)
	_ = a.WriteJSON(Message{Type: "connect", To: bid, From: "FORGED", Body: hello})
	pending := readMessage(t, a)
	connect := readMessage(t, b)
	if pending.Session == "" || connect.Session != pending.Session || connect.From == "FORGED" {
		t.Fatal("authenticated routing")
	}
	sid := pending.Session
	_ = a.WriteJSON(Message{Type: "data", Session: sid, Body: json.RawMessage(`"opaque"`)})
	if readMessage(t, a).Type != "error" {
		t.Fatal("data before authorization")
	}
	_ = c.WriteJSON(Message{Type: "close", Session: sid})
	if readMessage(t, c).Type != "error" {
		t.Fatal("third party hijack")
	}
	_ = b.WriteJSON(Message{Type: "accept", Session: sid})
	if readMessage(t, b).Type != "error" {
		t.Fatal("handshake ordering")
	}
	for _, step := range []struct {
		sender, target *websocket.Conn
		kind           string
	}{{b, a, "challenge"}, {a, b, "proof"}, {b, a, "accept"}, {a, b, "relay"}, {a, b, "data"}} {
		_ = step.sender.WriteJSON(Message{Type: step.kind, Session: sid, Body: json.RawMessage(`"opaque"`)})
		if readMessage(t, step.target).Type != step.kind {
			t.Fatal("route " + step.kind)
		}
	}
	a.Close()
	if readMessage(t, b).Type != "close" {
		t.Fatal("disconnect cleanup")
	}
}
func TestAggregateBudgetAndPendingExpiry(t *testing.T) {
	r, err := newRelay(":memory:", "")
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	r.mu.Lock()
	r.sessions["expired"] = &session{origin: "a", owner: "b", expires: time.Now().Add(-time.Second)}
	r.mu.Unlock()
	time.Sleep(1100 * time.Millisecond)
	r.mu.Lock()
	_, exists := r.sessions["expired"]
	r.mu.Unlock()
	if exists {
		t.Fatal("pending session leak")
	}
}

func TestAggregateBidirectionalRate(t *testing.T) {
	r, err := newRelay(":memory:", "")
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	s := httptest.NewServer(r.router())
	defer s.Close()
	ac, bc := randomCredential(), randomCredential()
	aid, bid := registerDevice(t, s.URL, ac, ""), registerDevice(t, s.URL, bc, "")
	a, b := socketDevice(t, s.URL, aid, ac), socketDevice(t, s.URL, bid, bc)
	_ = a.WriteJSON(Message{Type: "connect", To: bid, Body: json.RawMessage(`{"requestId":"01234567890123456789012345678901"}`)})
	sid := readMessage(t, a).Session
	readMessage(t, b)
	for _, step := range []struct {
		sender, target *websocket.Conn
		kind           string
	}{{b, a, "challenge"}, {a, b, "proof"}, {b, a, "accept"}} {
		_ = step.sender.WriteJSON(Message{Type: step.kind, Session: sid})
		readMessage(t, step.target)
	}
	payload := json.RawMessage(`"` + strings.Repeat("A", 62000) + `"`)
	start := time.Now()
	done := make(chan error, 2)
	for _, conn := range []*websocket.Conn{a, b} {
		go func(c *websocket.Conn) {
			for i := 0; i < 3; i++ {
				if e := c.WriteJSON(Message{Type: "data", Session: sid, Body: payload}); e != nil {
					done <- e
					return
				}
			}
			done <- nil
		}(conn)
	}
	for i := 0; i < 3; i++ {
		if readMessage(t, a).Type != "data" || readMessage(t, b).Type != "data" {
			t.Fatal("bidirectional data")
		}
	}
	for i := 0; i < 2; i++ {
		if e := <-done; e != nil {
			t.Fatal(e)
		}
	}
	elapsed := time.Since(start)
	if elapsed < 2*time.Second || elapsed > 8*time.Second {
		t.Fatalf("aggregate limiter elapsed %v", elapsed)
	}
}

func TestConfiguration(t *testing.T) {
	for _, key := range []string{"EMBEDDESK_CONFIG", "EMBEDDESK_LISTEN", "EMBEDDESK_DATABASE", "EMBEDDESK_ADMISSION_KEY", "EMBEDDESK_BANDWIDTH_BITS_PER_SECOND"} {
		t.Setenv(key, "")
	}
	path := filepath.Join(t.TempDir(), "relay.json")
	t.Setenv("EMBEDDESK_CONFIG", path)
	if err := os.WriteFile(path, []byte(`{"listen":"127.0.0.1:7081","database":":memory:","bandwidthBitsPerSecond":2345678}`), 0600); err != nil {
		t.Fatal(err)
	}
	config, err := loadRelayConfig()
	if err != nil || config.Listen != "127.0.0.1:7081" || config.BandwidthBitsPerSecond != 2345678 {
		t.Fatalf("load configuration: %v", err)
	}
	t.Setenv("EMBEDDESK_BANDWIDTH_BITS_PER_SECOND", "2000000")
	config, err = loadRelayConfig()
	if err != nil || config.BandwidthBitsPerSecond != 2000000 {
		t.Fatal("environment override")
	}
	t.Setenv("EMBEDDESK_BANDWIDTH_BITS_PER_SECOND", "")
	for _, bad := range []string{`null`, `[]`, `{"bandwidthBitsPerSecond":0}`, `{"bandwidthBitsPerSecond":-1}`, `{"bandwidthBitsPerSecond":100000000001}`, `{"bandwidthBitsPerSecond":1.5}`, `{"bandwith":1000000}`, `{} {}`, `{"listen":"bad"}`} {
		if err := os.WriteFile(path, []byte(bad), 0600); err != nil {
			t.Fatal(err)
		}
		if _, err := loadRelayConfig(); err == nil {
			t.Fatalf("accepted invalid configuration %s", bad)
		}
	}
}

func TestConfiguredBandwidthAndHeartbeatDuringThrottle(t *testing.T) {
	r, err := newRelayWithConfig(RelayConfig{Listen: "127.0.0.1:7080", Database: ":memory:", BandwidthBitsPerSecond: 8000})
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	s := httptest.NewServer(r.router())
	defer s.Close()
	credential := randomCredential()
	body, _ := json.Marshal(map[string]string{"credential": credential})
	response, err := http.Post(s.URL+"/v1/register", "application/json", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	var registered struct {
		ID   string `json:"id"`
		Bits int64  `json:"bitsPerSecond"`
	}
	if err := json.NewDecoder(response.Body).Decode(&registered); err != nil || registered.Bits != 8000 {
		t.Fatal("configured registration bandwidth")
	}
	socket := socketDevice(t, s.URL, registered.ID, credential)
	r.mu.Lock()
	p := r.peers[registered.ID]
	r.mu.Unlock()
	if float64(p.budget.Limit()) != 1000 {
		t.Fatal("configured limiter")
	}
	// Exhaust the burst and put both directions into a long wait, without blocking heartbeats.
	if !p.budget.AllowN(time.Now(), 64*1024) {
		t.Fatal("initial burst")
	}
	p.send(Message{Type: "data", Body: json.RawMessage(`"` + strings.Repeat("A", 62000) + `"`)})
	if err := socket.WriteJSON(Message{Type: "data", Session: "missing", Body: json.RawMessage(`"` + strings.Repeat("B", 62000) + `"`)}); err != nil {
		t.Fatal(err)
	}
	if err := socket.WriteJSON(Message{Type: "heartbeat"}); err != nil {
		t.Fatal(err)
	}
	if readMessage(t, socket).Type != "heartbeat" {
		t.Fatal("heartbeat was blocked by data quota")
	}
}
