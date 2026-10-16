// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
)

func TestRelayConnectionCode(t *testing.T) {
	code, err := relayConnectionCode("测试中继", "https://relay.example.test/prefix/", "test-token+/=")
	if err != nil {
		t.Fatal(err)
	}
	data, err := base64.StdEncoding.DecodeString(strings.TrimPrefix(code, "EMBEDDESK-RELAY:"))
	if err != nil {
		t.Fatal(err)
	}
	var payload map[string]any
	if err = json.Unmarshal(data, &payload); err != nil {
		t.Fatal(err)
	}
	if len(payload) != 4 || payload["version"] != float64(1) || payload["name"] != "测试中继" || payload["url"] != "https://relay.example.test/prefix" || payload["admissionToken"] != "test-token+/=" {
		t.Fatal("unexpected connection code fields")
	}
	if _, err = relayConnectionCode("Open relay", "http://127.0.0.1:7080", ""); err != nil {
		t.Fatal("empty admission token must remain supported")
	}
}

func TestRejectInvalidConnectionCodeInput(t *testing.T) {
	for _, input := range []struct{ name, address, token string }{
		{"", "https://relay.example.test", ""},
		{strings.Repeat("x", 65), "https://relay.example.test", ""},
		{"Relay", "https://user:secret@relay.example.test", ""},
		{"Relay", "https://relay.example.test?token=test", ""},
		{"Relay", "https://relay.example.test#fragment", ""},
		{"Relay", "file:///relay", ""},
		{"Relay", "http://127.0.0.1:0", ""},
		{"Relay", "http://127.0.0.1:65536", ""},
		{"Relay", "https://relay.example.test", "line\r\nbreak"},
		{"Relay", "https://relay.example.test", "with space"},
		{"Relay", "https://relay.example.test", strings.Repeat("x", 2049)},
		{"Relay", "https://relay.example.test", strings.Repeat("<", 2048)},
	} {
		if _, err := relayConnectionCode(input.name, input.address, input.token); err == nil {
			t.Fatal("invalid connection code input accepted")
		}
	}
}
