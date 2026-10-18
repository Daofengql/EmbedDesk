// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"image/png"
	"io"
	"io/fs"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/redis/go-redis/v9"
)

const testConsolePassword = "test-console-password-123"
const testAdmission = "test-node-admission"

type consoleClient struct {
	t      *testing.T
	base   string
	client *http.Client
	csrf   string
}

func (c *consoleClient) request(method, path string, body any, want int) map[string]any {
	c.t.Helper()
	var data []byte
	if body != nil {
		data = mustJSON(c.t, body)
	}
	req, _ := http.NewRequest(method, c.base+"/api/console"+path, bytes.NewReader(data))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-CSRF-Token", c.csrf)
	resp, err := c.client.Do(req)
	if err != nil {
		c.t.Fatal(err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != want {
		c.t.Fatalf("%s %s: got %d, want %d; %s", method, path, resp.StatusCode, want, raw)
	}
	var result map[string]any
	if want == 404 && !json.Valid(raw) {
		return nil
	}
	if err := json.Unmarshal(raw, &result); err != nil {
		c.t.Fatalf("invalid JSON %s", raw)
	}
	return result
}
func newConsoleClient(t *testing.T, base string) *consoleClient {
	jar, _ := cookiejar.New(nil)
	return &consoleClient{t: t, base: base, client: &http.Client{Jar: jar, Timeout: 5 * time.Second}}
}
func (c *consoleClient) login(name string) map[string]any {
	r := c.request("POST", "/auth/login", map[string]any{"username": name, "password": testConsolePassword}, 200)
	c.csrf = r["csrfToken"].(string)
	return r
}
func consoleFixture(t *testing.T) (*Relay, *httptest.Server, *consoleClient) {
	t.Helper()
	config := defaultRelayConfig()
	config.Database = ":memory:"
	config.ConsoleEnabled = true
	config.RegistrationEnabled = true
	config.CookieSecure = false
	config.AdmissionKey = testAdmission
	config.BootstrapUsername = "testadmin"
	config.BootstrapPassword = testConsolePassword
	r, err := newRelayWithConfig(config)
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(r.router())
	t.Cleanup(func() { s.Close(); _ = r.close() })
	admin := newConsoleClient(t, s.URL)
	admin.login("testadmin")
	return r, s, admin
}
func createTestUser(c *consoleClient, name, role string) uint {
	v := c.request("POST", "/users", map[string]any{"username": name, "displayName": name, "password": testConsolePassword, "role": role, "maxDevices": 2, "bandwidthBitsPerSecond": 1000000}, 201)
	return uint(v["id"].(float64))
}
func mustJSON(t *testing.T, v any) []byte {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return b
}
func registerWithStatus(t *testing.T, base, credential, admission string, want int) map[string]any {
	t.Helper()
	req, _ := http.NewRequest("POST", base+"/v1/register", bytes.NewReader(mustJSON(t, map[string]string{"credential": credential})))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+admission)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	var v map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&v)
	if resp.StatusCode != want {
		t.Fatalf("register status %d want %d: %v", resp.StatusCode, want, v)
	}
	return v
}
func statusDeviceRequest(t *testing.T, method, base, path, credential, id string, want int) {
	t.Helper()
	req, _ := http.NewRequest(method, base+path, nil)
	req.Header.Set("Authorization", "Bearer "+credential)
	req.Header.Set("X-Device-ID", id)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != want {
		t.Fatalf("device request got %d want %d", resp.StatusCode, want)
	}
}
func closedPeer(t *testing.T, r *Relay, id string, conn *websocket.Conn) {
	t.Helper()
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	var m Message
	if conn.ReadJSON(&m) == nil {
		t.Fatal("disconnected socket remained open")
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		r.mu.Lock()
		p := r.peers[id]
		r.mu.Unlock()
		if p == nil {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatal("disconnected peer retained")
}
func handshake(t *testing.T, a, b *websocket.Conn, bid string) string {
	t.Helper()
	_ = a.WriteJSON(Message{Type: "connect", To: bid, Body: json.RawMessage(`{"requestId":"01234567890123456789012345678901"}`)})
	sid := readMessage(t, a).Session
	readMessage(t, b)
	for _, step := range []struct {
		from, to *websocket.Conn
		kind     string
	}{{b, a, "challenge"}, {a, b, "proof"}, {b, a, "accept"}} {
		_ = step.from.WriteJSON(Message{Type: step.kind, Session: sid})
		readMessage(t, step.to)
	}
	return sid
}
func seedCaptcha(t *testing.T, a *Console) string {
	t.Helper()
	id, err := randomSecret()
	if err != nil {
		t.Fatal(err)
	}
	if err = a.putCaptcha(context.Background(), id, "123456"); err != nil {
		t.Fatal(err)
	}
	return id
}

func TestConsoleRolesBindingIsolationAndSessions(t *testing.T) {
	r, s, admin := consoleFixture(t)
	createTestUser(admin, "alice", "user")
	createTestUser(admin, "bobby", "user")
	createTestUser(admin, "guest", "guest")
	alice, bob, guest := newConsoleClient(t, s.URL), newConsoleClient(t, s.URL), newConsoleClient(t, s.URL)
	alice.login("alice")
	bob.login("bobby")
	guest.login("guest")
	alice.request("GET", "/users", nil, 403)
	guest.request("GET", "/audit", nil, 403)
	for _, c := range []*consoleClient{admin, alice, bob, guest} {
		if c.request("GET", "/relay", nil, 200)["admissionToken"] != testAdmission {
			t.Fatal("node token must be shared")
		}
		c.request("GET", "/tokens", nil, 404)
	}
	alice.request("PATCH", "/relay", map[string]any{"admissionToken": "forbidden"}, 403)
	ac, bc := randomCredential(), randomCredential()
	aid, bid := registerDevice(t, s.URL, ac, testAdmission), registerDevice(t, s.URL, bc, testAdmission)
	if alice.request("GET", "/devices", nil, 200)["total"].(float64) != 0 {
		t.Fatal("anonymous devices leaked to user")
	}
	guest.request("POST", "/devices/bind", map[string]any{"deviceId": aid}, 403)
	alice.request("POST", "/devices/bind", map[string]any{"deviceId": strings.ToLower(aid)}, 200)
	alice.request("POST", "/devices/bind", map[string]any{"deviceId": aid}, 200)
	bob.request("POST", "/devices/bind", map[string]any{"deviceId": aid}, 409)
	bob.request("POST", "/devices/bind", map[string]any{"deviceId": bid}, 200)
	bob.request("POST", "/devices/bind", map[string]any{"deviceId": "000000000000"}, 404)
	alice.request("PATCH", "/devices/"+aid, map[string]any{"ownerId": nil}, 403)
	bob.request("PATCH", "/devices/"+aid, map[string]any{"disabled": true}, 404)
	guest.request("PATCH", "/devices/"+aid, map[string]any{"disabled": true}, 403)
	if alice.request("GET", "/devices?q="+bid, nil, 200)["total"].(float64) != 0 {
		t.Fatal("cross-user devices leaked")
	}
	a, b := socketDevice(t, s.URL, aid, ac), socketDevice(t, s.URL, bid, bc)
	sid := handshake(t, a, b, bid)
	if admin.request("GET", "/sessions", nil, 200)["total"].(float64) != 1 {
		t.Fatal("admin session visibility")
	}
	if alice.request("GET", "/sessions", nil, 200)["total"].(float64) != 1 {
		t.Fatal("owner session visibility")
	}
	if guest.request("GET", "/sessions", nil, 200)["total"].(float64) != 0 {
		t.Fatal("guest session leak")
	}
	guest.request("POST", "/sessions/"+sid+"/close", map[string]any{}, 403)
	admin.request("POST", "/sessions/"+sid+"/close", map[string]any{}, 200)
	if readMessage(t, a).Type != "close" || readMessage(t, b).Type != "close" {
		t.Fatal("both peers must receive close")
	}
	if admin.request("GET", "/sessions", nil, 200)["total"].(float64) != 0 {
		t.Fatal("session retained")
	}
	// Rotating the public filter affects registration only, not authenticated sockets.
	rotatedToken := admin.request("POST", "/relay/admission-token/refresh", map[string]any{}, 200)["admissionToken"].(string)
	_ = a.WriteJSON(Message{Type: "heartbeat"})
	if readMessage(t, a).Type != "heartbeat" {
		t.Fatal("token rotation interrupted peer")
	}
	registerWithStatus(t, s.URL, randomCredential(), testAdmission, 401)
	registerWithStatus(t, s.URL, randomCredential(), rotatedToken, 200)
	admin.request("PATCH", "/devices/"+aid, map[string]any{"disabled": true}, 200)
	closedPeer(t, r, aid, a)
	statusDeviceRequest(t, "GET", s.URL, "/v1/devices/"+bid, ac, aid, 403)
	registerWithStatus(t, s.URL, ac, rotatedToken, 403)
	var events []AuditEvent
	if r.db.Find(&events).Error != nil {
		t.Fatal("audit unavailable")
	}
	for _, secret := range []string{testConsolePassword, ac, testAdmission, rotatedToken} {
		if bytes.Contains(mustJSON(t, events), []byte(secret)) {
			t.Fatal("audit contains secret")
		}
	}
}

func TestBoundDevicesShareAccountBudgetAndQuota(t *testing.T) {
	r, s, admin := consoleFixture(t)
	uid := createTestUser(admin, "limited", "user")
	user := newConsoleClient(t, s.URL)
	user.login("limited")
	ac, bc, cc := randomCredential(), randomCredential(), randomCredential()
	aid, bid, cid := registerDevice(t, s.URL, ac, testAdmission), registerDevice(t, s.URL, bc, testAdmission), registerDevice(t, s.URL, cc, testAdmission)
	a, b := socketDevice(t, s.URL, aid, ac), socketDevice(t, s.URL, bid, bc)
	r.mu.Lock()
	separate := r.peers[aid].budget != r.peers[bid].budget
	r.mu.Unlock()
	if !separate {
		t.Fatal("anonymous budgets were shared")
	}
	user.request("POST", "/devices/bind", map[string]any{"deviceId": aid}, 200)
	closedPeer(t, r, aid, a)
	user.request("POST", "/devices/bind", map[string]any{"deviceId": bid}, 200)
	closedPeer(t, r, bid, b)
	user.request("POST", "/devices/bind", map[string]any{"deviceId": cid}, 409)
	a, b = socketDevice(t, s.URL, aid, ac), socketDevice(t, s.URL, bid, bc)
	r.mu.Lock()
	shared := r.peers[aid].budget == r.peers[bid].budget
	r.mu.Unlock()
	if !shared {
		t.Fatal("account quota is not shared")
	}
	sid := handshake(t, a, b, bid)
	payload := json.RawMessage(`"` + strings.Repeat("A", 62000) + `"`)
	start := time.Now()
	for i := 0; i < 3; i++ {
		if err := a.WriteJSON(Message{Type: "data", Session: sid, Body: payload}); err != nil {
			t.Fatal(err)
		}
	}
	for i := 0; i < 3; i++ {
		if readMessage(t, b).Type != "data" {
			t.Fatal("data transfer failed")
		}
	}
	if elapsed := time.Since(start); elapsed < 2*time.Second || elapsed > 8*time.Second {
		t.Fatalf("shared RX/TX quota elapsed %v", elapsed)
	}
	admin.request("PATCH", "/users/"+userTarget(uid), map[string]any{"bandwidthBitsPerSecond": 2000000}, 200)
	// Closing either peer may also send a close message to its partner.
	for _, conn := range []*websocket.Conn{a, b} {
		_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
		for {
			var m Message
			if conn.ReadJSON(&m) != nil {
				break
			}
		}
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		r.mu.Lock()
		empty := r.peers[aid] == nil && r.peers[bid] == nil
		r.mu.Unlock()
		if empty {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	a, b = socketDevice(t, s.URL, aid, ac), socketDevice(t, s.URL, bid, bc)
	r.mu.Lock()
	correct := r.peers[aid].budget == r.peers[bid].budget && float64(r.peers[aid].budget.Limit()) == 250000
	r.mu.Unlock()
	if !correct {
		t.Fatal("increased account quota not applied")
	}
	admin.request("PATCH", "/devices/"+bid, map[string]any{"ownerId": nil}, 200)
	var d Device
	r.db.First(&d, "id = ?", bid)
	if d.OwnerID != nil {
		t.Fatal("admin unbind failed")
	}
	admin.request("PATCH", "/devices/"+cid, map[string]any{"ownerId": 999999}, 400)
}

func TestImageCaptchaRegistrationAndDefaultBandwidth(t *testing.T) {
	r, s, admin := consoleFixture(t)
	visitor := newConsoleClient(t, s.URL)
	if !visitor.request("GET", "/auth/options", nil, 200)["registrationEnabled"].(bool) {
		t.Fatal("registration option")
	}
	image := visitor.request("GET", "/auth/captcha", nil, 200)
	encoded := strings.TrimPrefix(image["image"].(string), "data:image/png;base64,")
	raw, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = png.Decode(bytes.NewReader(raw)); err != nil {
		t.Fatal("invalid captcha PNG")
	}
	admin.request("PATCH", "/relay", map[string]any{"defaultBandwidthBitsPerSecond": 3000000}, 200)
	body := map[string]any{"username": "newuser", "password": testConsolePassword, "role": "admin", "maxDevices": 999, "bandwidthBitsPerSecond": 99999999}
	visitor.request("POST", "/auth/register", body, 400)
	body["captchaId"] = seedCaptcha(t, r.console)
	body["captchaCode"] = "wrong"
	visitor.request("POST", "/auth/register", body, 400)
	body["captchaCode"] = "123456"
	visitor.request("POST", "/auth/register", body, 400)
	body["captchaId"] = seedCaptcha(t, r.console)
	u := visitor.request("POST", "/auth/register", body, 201)
	if u["role"] != "user" || u["maxDevices"].(float64) != 5 || u["bandwidthBitsPerSecond"].(float64) != 3000000 {
		t.Fatal("registration escalation/default quota")
	}
	body["username"] = "replayuser"
	visitor.request("POST", "/auth/register", body, 400)
	visitor.login("newuser")
	visitor.request("GET", "/users", nil, 403)
	admin.request("PATCH", "/users/"+userTarget(uint(u["id"].(float64))), map[string]any{"bandwidthBitsPerSecond": 5000000}, 200)
	admin.request("PATCH", "/relay", map[string]any{"defaultBandwidthBitsPerSecond": 4000000}, 200)
	me := visitor.request("GET", "/auth/me", nil, 200)["user"].(map[string]any)
	if me["bandwidthBitsPerSecond"].(float64) != 5000000 {
		t.Fatal("default change overwrote individual quota")
	}
	created := admin.request("POST", "/users", map[string]any{"username": "defaulted", "displayName": "Default", "password": testConsolePassword, "role": "user", "maxDevices": 2}, 201)
	if created["bandwidthBitsPerSecond"].(float64) != 4000000 {
		t.Fatal("admin-created user default")
	}
	anon := registerWithStatus(t, s.URL, randomCredential(), testAdmission, 200)
	if anon["bitsPerSecond"].(float64) != 4000000 {
		t.Fatal("anonymous default bandwidth")
	}
	admin.request("PATCH", "/relay", map[string]any{"defaultBandwidthBitsPerSecond": 0}, 400)
	admin.request("PATCH", "/relay", map[string]any{"admissionToken": "with spaces"}, 400)
	expired := seedCaptcha(t, r.console)
	r.db.Model(&CaptchaChallenge{}).Where("id = ?", hashCredential(expired)).Update("expires_at", time.Now().Add(-time.Second))
	if ok, err := r.console.consumeCaptcha(context.Background(), expired, "123456"); err != nil || ok {
		t.Fatal("expired captcha accepted")
	}
	assertSingleCaptchaUse(t, r.console)
}
func assertSingleCaptchaUse(t *testing.T, a *Console) {
	t.Helper()
	id := seedCaptcha(t, a)
	var wins atomic.Int32
	var group sync.WaitGroup
	for i := 0; i < 4; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			ok, err := a.consumeCaptcha(context.Background(), id, "123456")
			if err != nil {
				t.Error(err)
			}
			if ok {
				wins.Add(1)
			}
		}()
	}
	group.Wait()
	if wins.Load() != 1 {
		t.Fatalf("captcha accepted %d times", wins.Load())
	}
}

func TestConsoleCSRFAdminProtectionAndSessionRevocation(t *testing.T) {
	_, s, admin := consoleFixture(t)
	csrf := admin.csrf
	admin.csrf = ""
	admin.request("PATCH", "/relay", map[string]any{"name": "forbidden"}, 403)
	admin.csrf = csrf
	admin.request("PATCH", "/users/1", map[string]any{"disabled": true}, 409)
	admin.request("PATCH", "/users/1", map[string]any{"role": "guest"}, 409)
	id := createTestUser(admin, "sessionuser", "user")
	user := newConsoleClient(t, s.URL)
	user.login("sessionuser")
	admin.request("PATCH", "/users/"+userTarget(id), map[string]any{"role": "guest"}, 200)
	user.request("GET", "/auth/me", nil, 401)
	user.login("sessionuser")
	user.request("POST", "/auth/logout", map[string]any{}, 200)
	user.request("GET", "/auth/me", nil, 401)
	req, _ := http.NewRequest("POST", s.URL+"/api/console/auth/login", strings.NewReader(`{"username":"testadmin","password":"test-console-password-123"}`))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Origin", "https://untrusted.invalid")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 403 {
		t.Fatal("cross-origin login accepted")
	}
	admin.request("POST", "/auth/password", map[string]any{"currentPassword": testConsolePassword, "newPassword": "replacement-password-123"}, 200)
	admin.request("GET", "/auth/me", nil, 401)
}

func TestNodeSettingsPersistAcrossRestart(t *testing.T) {
	config := defaultRelayConfig()
	config.Database = filepath.Join(t.TempDir(), "settings.sqlite")
	config.ConsoleEnabled = true
	config.CookieSecure = false
	config.BootstrapUsername = "testadmin"
	config.BootstrapPassword = testConsolePassword
	r, err := newRelayWithConfig(config)
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(r.router())
	admin := newConsoleClient(t, s.URL)
	admin.login("testadmin")
	admin.request("PATCH", "/relay", map[string]any{"name": "Saved node", "defaultBandwidthBitsPerSecond": 3000000}, 200)
	persistedToken := admin.request("POST", "/relay/admission-token/refresh", map[string]any{}, 200)["admissionToken"].(string)
	s.Close()
	_ = r.close()
	r, err = newRelayWithConfig(config)
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	settings := r.node.Load()
	if settings.Name != "Saved node" || settings.AdmissionKey != persistedToken || settings.DefaultBandwidthBitsPerSecond != 3000000 {
		t.Fatal("saved node policy was overwritten by seed config")
	}
}

func TestEmbeddedConsoleRoutes(t *testing.T) {
	if _, err := fs.Stat(consoleAssets, "www/dist/index.html"); err != nil {
		t.Skip("build server/www to verify embedded console")
	}
	_, s, _ := consoleFixture(t)
	for _, path := range []string{"/console", "/console/", "/console/index.html", "/console/devices"} {
		resp, err := http.Get(s.URL + path)
		if err != nil {
			t.Fatal(err)
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		if resp.StatusCode != 200 || !bytes.Contains(body, []byte("EmbedDesk")) || resp.Header.Get("Content-Security-Policy") == "" {
			t.Fatalf("embedded route %s failed: %d", path, resp.StatusCode)
		}
	}
	resp, err := http.Get(s.URL + "/console/missing.js")
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 404 {
		t.Fatal("missing asset returned SPA HTML")
	}
}

// Run only against a dedicated integration database. No unrelated tables or Redis keys are removed.
func TestExternalMySQLRedis(t *testing.T) {
	dsn, redisURL := os.Getenv("EMBEDDESK_TEST_MYSQL_DSN"), os.Getenv("EMBEDDESK_TEST_REDIS_URL")
	if dsn == "" || redisURL == "" {
		t.Skip("dedicated MySQL and Redis integration targets not configured")
	}
	config := defaultRelayConfig()
	config.DatabaseDriver = "mysql"
	config.MySQLDSN = dsn
	config.RedisURL = redisURL
	secret, _ := randomSecret()
	config.RedisPrefix = "embeddesk-test:" + secret + ":"
	config.ConsoleEnabled = true
	config.RegistrationEnabled = true
	config.CookieSecure = false
	config.BootstrapUsername = "testadmin"
	config.BootstrapPassword = testConsolePassword
	r, err := newRelayWithConfig(config)
	if err != nil {
		t.Fatal(err)
	}
	defer r.close()
	s := httptest.NewServer(r.router())
	defer s.Close()
	admin := newConsoleClient(t, s.URL)
	admin.login("testadmin")
	admin.request("GET", "/auth/me", nil, 200)
	suffix := strings.ToLower(hashCredential(secret)[:10])
	id := createTestUser(admin, "external"+suffix, "user")
	user := newConsoleClient(t, s.URL)
	user.login("external" + suffix)
	cred := randomCredential()
	did := registerWithStatus(t, s.URL, cred, r.node.Load().AdmissionKey, 200)["id"].(string)
	user.request("POST", "/devices/bind", map[string]any{"deviceId": did}, 200)
	conn := socketDevice(t, s.URL, did, cred)
	if user.request("GET", "/devices?q="+did, nil, 200)["total"].(float64) != 1 {
		t.Fatal("MySQL owner listing")
	}
	assertSingleCaptchaUse(t, r.console)
	visitor := newConsoleClient(t, s.URL)
	visitor.request("GET", "/auth/captcha", nil, 200)
	v := visitor.request("POST", "/auth/register", map[string]any{"username": "signup" + suffix, "password": testConsolePassword, "captchaId": seedCaptcha(t, r.console), "captchaCode": "123456"}, 201)
	if v["bandwidthBitsPerSecond"].(float64) != float64(r.node.Load().DefaultBandwidthBitsPerSecond) {
		t.Fatal("MySQL registration default")
	}
	admin.request("PATCH", "/users/"+userTarget(id), map[string]any{"disabled": true}, 200)
	closedPeer(t, r, did, conn)
	statusDeviceRequest(t, "GET", s.URL, "/v1/devices/"+did, cred, did, 403)
	keys, err := r.console.redis.Keys(context.Background(), config.RedisPrefix+"session:*").Result()
	if err != nil || len(keys) == 0 {
		t.Fatal("Redis sessions missing")
	}
	good := r.console.redis
	r.console.redis = redis.NewClient(&redis.Options{Addr: "127.0.0.1:1", DialTimeout: 100 * time.Millisecond, MaxRetries: -1})
	admin.request("GET", "/auth/me", nil, 503)
	_ = r.console.redis.Close()
	r.console.redis = good
	admin.request("POST", "/auth/logout", map[string]any{}, 200)
	keys, _ = good.Keys(context.Background(), config.RedisPrefix+"*").Result()
	if len(keys) > 0 {
		if err := good.Del(context.Background(), keys...).Err(); err != nil {
			t.Fatal(err)
		}
	}
}
