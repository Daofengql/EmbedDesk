// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"strings"
	"testing"

	"github.com/gorilla/websocket"
)

func TestBatchUsersAtomicAdminProtectionAndQuota(t *testing.T) {
	r, s, admin := consoleFixture(t)
	otherAdmin := createTestUser(admin, "otheradmin", "admin")
	u1, u2 := createTestUser(admin, "batchalice", "user"), createTestUser(admin, "batchbobby", "user")
	user := newConsoleClient(t, s.URL)
	user.login("batchalice")
	var original User
	r.db.Where("username = ?", "testadmin").First(&original)
	admin.request("POST", "/users/batch", map[string]any{"ids": []uint{original.ID, otherAdmin}, "action": "disable"}, 409)
	var enabled int64
	r.db.Model(&User{}).Where("role = ? AND disabled = ?", "admin", false).Count(&enabled)
	if enabled != 2 {
		t.Fatal("batch disabled administrators despite rollback")
	}
	admin.request("POST", "/users/batch", map[string]any{"ids": []uint{u1, 999999}, "action": "disable"}, 404)
	user.request("GET", "/auth/me", nil, 200)
	user.request("POST", "/users/batch", map[string]any{"ids": []uint{u1}, "action": "disable"}, 403)
	for _, body := range []map[string]any{
		{"ids": []uint{}, "action": "disable"},
		{"ids": []uint{u1, u1}, "action": "disable"},
		{"ids": []uint{u1}, "action": "bandwidth", "bandwidthBitsPerSecond": 7999},
		{"ids": []uint{u1}, "action": "deviceLimit", "maxDevices": 0},
		{"ids": []uint{u1}, "action": "enable", "role": "admin"},
	} {
		admin.request("POST", "/users/batch", body, 400)
	}
	ids := []uint{u1, u2}
	admin.request("POST", "/users/batch", map[string]any{"ids": ids, "action": "bandwidth", "bandwidthBitsPerSecond": 4000000}, 200)
	admin.request("POST", "/users/batch", map[string]any{"ids": ids, "action": "deviceLimit", "maxDevices": 3}, 200)
	var users []User
	r.db.Where("id IN ?", ids).Find(&users)
	for _, u := range users {
		if u.BandwidthBitsPerSecond != 4000000 || u.MaxDevices != 3 {
			t.Fatal("batch quota not saved")
		}
	}
	admin.request("POST", "/users/batch", map[string]any{"ids": ids, "action": "disable"}, 200)
	user.request("GET", "/auth/me", nil, 401)
	admin.request("POST", "/users/batch", map[string]any{"ids": ids, "action": "enable"}, 200)
	user.login("batchalice")
	var events int64
	r.db.Model(&AuditEvent{}).Where("action = ?", "user.batch.disable").Count(&events)
	if events != 2 {
		t.Fatal("failed batch leaked audit or successful batch not audited per target")
	}
}

func TestBatchDevicesOwnershipRollbackAndDisconnect(t *testing.T) {
	r, s, admin := consoleFixture(t)
	uid := createTestUser(admin, "batchowner", "user")
	createTestUser(admin, "batchforeign", "user")
	createTestUser(admin, "batchguest", "guest")
	user, foreign, guest := newConsoleClient(t, s.URL), newConsoleClient(t, s.URL), newConsoleClient(t, s.URL)
	user.login("batchowner")
	foreign.login("batchforeign")
	guest.login("batchguest")
	credentials := []string{randomCredential(), randomCredential(), randomCredential()}
	ids := []string{}
	for _, c := range credentials {
		ids = append(ids, registerDevice(t, s.URL, c, testAdmission))
	}
	// The third assignment exceeds the account's two-device limit. Earlier writes and audits must roll back.
	admin.request("POST", "/devices/batch", map[string]any{"ids": ids, "action": "assign", "ownerId": uid}, 409)
	var count int64
	r.db.Model(&Device{}).Where("owner_id = ?", uid).Count(&count)
	if count != 0 {
		t.Fatal("partial device assignment escaped transaction")
	}
	admin.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "assign", "ownerId": uid}, 200)
	foreign.request("POST", "/devices/bind", map[string]any{"deviceId": ids[2]}, 200)
	user.request("POST", "/devices/batch", map[string]any{"ids": ids, "action": "delete"}, 404)
	if user.request("GET", "/devices", nil, 200)["total"].(float64) != 2 {
		t.Fatal("mixed ownership batch partially deleted devices")
	}
	guest.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "disable"}, 403)
	user.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "assign", "ownerId": nil}, 403)
	admin.request("POST", "/devices/batch", map[string]any{"ids": []string{ids[0], strings.ToLower(ids[0])}, "action": "disable"}, 400)
	a, b := socketDevice(t, s.URL, ids[0], credentials[0]), socketDevice(t, s.URL, ids[1], credentials[1])
	user.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "disconnect"}, 200)
	closedPeer(t, r, ids[0], a)
	closedPeer(t, r, ids[1], b)
	a, b = socketDevice(t, s.URL, ids[0], credentials[0]), socketDevice(t, s.URL, ids[1], credentials[1])
	user.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "disable"}, 200)
	closedPeer(t, r, ids[0], a)
	closedPeer(t, r, ids[1], b)
	for i := 0; i < 2; i++ {
		registerWithStatus(t, s.URL, credentials[i], testAdmission, 403)
	}
	user.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "enable"}, 200)
	admin.request("POST", "/devices/batch", map[string]any{"ids": ids[:2], "action": "assign", "ownerId": nil}, 200)
	if user.request("GET", "/devices", nil, 200)["total"].(float64) != 0 {
		t.Fatal("unassignment not applied")
	}
	admin.request("POST", "/devices/batch", map[string]any{"ids": ids, "action": "delete"}, 200)
	r.db.Model(&Device{}).Count(&count)
	if count != 0 {
		t.Fatal("batch deletion left registrations")
	}
}

func TestBatchSessionsIsolationNotificationsAndEndedSkip(t *testing.T) {
	r, s, admin := consoleFixture(t)
	createTestUser(admin, "sessionowner", "user")
	createTestUser(admin, "sessionguest", "guest")
	user, guest := newConsoleClient(t, s.URL), newConsoleClient(t, s.URL)
	user.login("sessionowner")
	guest.login("sessionguest")
	ids := []string{}
	var sockets []*websocket.Conn
	for i := 0; i < 4; i++ {
		credential := randomCredential()
		id := registerDevice(t, s.URL, credential, testAdmission)
		ids = append(ids, id)
		if i == 0 {
			user.request("POST", "/devices/bind", map[string]any{"deviceId": id}, 200)
		}
		sockets = append(sockets, socketDevice(t, s.URL, id, credential))
	}
	sids := []string{handshake(t, sockets[0], sockets[1], ids[1]), handshake(t, sockets[2], sockets[3], ids[3])}
	user.request("POST", "/sessions/batch", map[string]any{"ids": sids, "action": "close"}, 404)
	guest.request("POST", "/sessions/batch", map[string]any{"ids": sids, "action": "close"}, 403)
	r.mu.Lock()
	count := len(r.sessions)
	r.mu.Unlock()
	if count != 2 {
		t.Fatal("unauthorized batch closed visible session before rejecting foreign session")
	}
	sids = append(sids, strings.Repeat("0", 32))
	result := admin.request("POST", "/sessions/batch", map[string]any{"ids": sids, "action": "close"}, 200)
	if result["affected"].(float64) != 2 || result["skipped"].(float64) != 1 {
		t.Fatal("batch close count incorrect")
	}
	for _, c := range sockets {
		if readMessage(t, c).Type != "close" {
			t.Fatal("batch close did not notify both endpoints")
		}
	}
	result = admin.request("POST", "/sessions/batch", map[string]any{"ids": sids, "action": "close"}, 200)
	if result["affected"].(float64) != 0 || result["skipped"].(float64) != 3 {
		t.Fatal("ended sessions not skipped idempotently")
	}
}
