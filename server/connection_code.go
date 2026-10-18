// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/url"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf16"
)

// This code contains the admission secret. It is emitted only by the explicit export command.
func relayConnectionCode(name, address, token string) (string, error) {
	if strings.ContainsFunc(name, unicode.IsControl) {
		return "", errors.New("relay name must not contain control characters")
	}
	name = strings.TrimSpace(name)
	if length := len(utf16.Encode([]rune(name))); length < 1 || length > 64 || strings.ContainsFunc(name, unicode.IsControl) {
		return "", errors.New("relay name must contain 1-64 characters without control characters")
	}
	address = strings.TrimSpace(address)
	u, err := url.Parse(address)
	if err != nil || len(address) == 0 || len(address) > 2048 || strings.ContainsFunc(address, unicode.IsControl) ||
		(u.Scheme != "http" && u.Scheme != "https") || u.Hostname() == "" || u.User != nil || strings.ContainsAny(address, "?#") {
		return "", errors.New("relay URL must be HTTP or HTTPS without credentials, query or fragment")
	}
	if u.Port() != "" {
		port, portErr := strconv.Atoi(u.Port())
		if portErr != nil || port < 1 || port > 65535 {
			return "", errors.New("relay port must be between 1 and 65535")
		}
	}
	if len(token) > 2048 || strings.ContainsFunc(token, func(c rune) bool { return c < '!' || c > '~' }) {
		return "", errors.New("admission token must be at most 2048 ASCII characters without whitespace or controls")
	}
	payload := struct {
		Version        int    `json:"version"`
		Name           string `json:"name"`
		URL            string `json:"url"`
		AdmissionToken string `json:"admissionToken"`
	}{1, name, strings.TrimRight(address, "/"), token}
	data, err := json.Marshal(payload)
	if err != nil {
		return "", err
	}
	if len(data) > 8*1024 {
		return "", errors.New("connection code payload exceeds 8 KiB")
	}
	return "EMBEDDESK-RELAY:" + base64.StdEncoding.EncodeToString(data), nil
}
