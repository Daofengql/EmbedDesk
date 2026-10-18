// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
)

func TestConsoleAdmissionTokenRefresh(t *testing.T) {
	r, s, admin := consoleFixture(t)
	path := "/relay/admission-token/refresh"
	newConsoleClient(t, s.URL).request("POST", path, map[string]any{}, 401)
	for _, role := range []string{"user", "guest"} {
		createTestUser(admin, "refresh"+role, role)
		client := newConsoleClient(t, s.URL)
		client.login("refresh" + role)
		client.request("POST", path, map[string]any{}, 403)
	}
	csrf := admin.csrf
	admin.csrf = ""
	admin.request("POST", path, map[string]any{}, 403)
	admin.csrf = csrf
	admin.request("PATCH", "/relay", map[string]any{"admissionToken": "manual-token"}, 400)
	if r.node.Load().AdmissionKey != testAdmission {
		t.Fatal("rejected refresh or manual input changed the token")
	}
	admin.request("PATCH", "/relay", map[string]any{"publicUrl": s.URL}, 200)
	previous := testAdmission
	for range 2 {
		response := admin.request("POST", path, map[string]any{}, 200)
		token := response["admissionToken"].(string)
		decoded, err := base64.RawURLEncoding.DecodeString(token)
		if err != nil || len(decoded) != 32 || token == previous || response["admissionRequired"] != true {
			t.Fatal("refresh must generate a new 32-byte token and enable admission")
		}
		var saved NodeSettings
		if r.db.First(&saved, 1).Error != nil || saved.AdmissionKey != token || r.node.Load().AdmissionKey != token {
			t.Fatal("refreshed token was not committed to database and live policy")
		}
		code, err := base64.StdEncoding.DecodeString(strings.TrimPrefix(response["connectionCode"].(string), "EMBEDDESK-RELAY:"))
		var payload map[string]any
		if err != nil || json.Unmarshal(code, &payload) != nil || payload["admissionToken"] != token {
			t.Fatal("connection code does not contain the refreshed token")
		}
		registerWithStatus(t, s.URL, randomCredential(), previous, 401)
		registerWithStatus(t, s.URL, randomCredential(), token, 200)
		previous = token
	}
	admin.request("PATCH", "/relay", map[string]any{"admissionEnabled": true, "name": "Updated node"}, 200)
	if r.node.Load().AdmissionKey != previous {
		t.Fatal("saving an enabled filter unexpectedly rotated the token")
	}
	admin.request("PATCH", "/relay", map[string]any{"admissionEnabled": false}, 200)
	registerWithStatus(t, s.URL, randomCredential(), "", 200)
	admin.request("PATCH", "/relay", map[string]any{"admissionEnabled": true}, 200)
	if token := r.node.Load().AdmissionKey; len(token) != 43 || token == previous {
		t.Fatal("enabling admission must generate a new token")
	}
	admin.request("PATCH", "/relay", map[string]any{"admissionEnabled": false}, 200)
	if admin.request("POST", path, map[string]any{}, 200)["admissionRequired"] != true {
		t.Fatal("refreshing a disabled filter must enable it")
	}
	var events []AuditEvent
	if r.db.Where("action = ?", "relay.token.refresh").Find(&events).Error != nil || len(events) != 3 {
		t.Fatal("refresh audit event missing")
	}
	for _, event := range events {
		if event.Target != "node" {
			t.Fatal("refresh audit must identify the node without including token data")
		}
	}
}
