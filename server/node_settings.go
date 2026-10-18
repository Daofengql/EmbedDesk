// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/json"
	"errors"
	"strings"

	"github.com/gin-gonic/gin"
	"gorm.io/gorm"
)

type NodeSettings struct {
	ID                            uint   `gorm:"primaryKey"`
	Name                          string `gorm:"size:64;not null"`
	PublicURL                     string `gorm:"size:2048;not null"`
	AdmissionKey                  string `gorm:"size:2048;not null"`
	DefaultBandwidthBitsPerSecond int64
}

func initialNodeSettings(config RelayConfig) NodeSettings {
	name := config.RelayName
	if name == "" {
		name = "EmbedDesk 中继"
	}
	return NodeSettings{ID: 1, Name: name, PublicURL: strings.TrimRight(config.PublicURL, "/"), AdmissionKey: config.AdmissionKey, DefaultBandwidthBitsPerSecond: config.BandwidthBitsPerSecond}
}
func validateNodeSettings(s NodeSettings) error {
	if s.DefaultBandwidthBitsPerSecond < minBandwidthBitsPerSecond || s.DefaultBandwidthBitsPerSecond > maxBandwidthBitsPerSecond {
		return errors.New("default bandwidth must be 8000-100000000000 bits/s")
	}
	address := s.PublicURL
	if address == "" {
		address = "http://127.0.0.1"
	}
	if _, err := relayConnectionCode(s.Name, address, s.AdmissionKey); err != nil {
		return err
	}
	if s.PublicURL != "" {
		config := defaultRelayConfig()
		config.PublicURL = s.PublicURL
		config.RelayName = s.Name
		if err := config.validateConsole(); err != nil {
			return err
		}
	}
	return nil
}
func (r *Relay) loadNodeSettings() error {
	settings := *r.node.Load()
	if err := r.db.Where("id = ?", 1).FirstOrCreate(&settings).Error; err != nil {
		return err
	}
	if err := validateNodeSettings(settings); err != nil {
		return err
	}
	r.node.Store(&settings)
	return nil
}

func nodeSettingsResponse(s NodeSettings) gin.H {
	code := ""
	if s.PublicURL != "" {
		code, _ = relayConnectionCode(s.Name, s.PublicURL, s.AdmissionKey)
	}
	return gin.H{"name": s.Name, "publicUrl": s.PublicURL, "admissionRequired": s.AdmissionKey != "", "admissionToken": s.AdmissionKey, "connectionCode": code, "defaultBandwidthBitsPerSecond": s.DefaultBandwidthBitsPerSecond}
}
func (a *Console) relaySettings(c *gin.Context) { c.JSON(200, nodeSettingsResponse(*a.r.node.Load())) }
func (a *Console) updateRelaySettings(c *gin.Context) {
	var in struct {
		Name             *string `json:"name"`
		PublicURL        *string `json:"publicUrl"`
		AdmissionEnabled *bool   `json:"admissionEnabled"`
		DefaultBandwidth *int64  `json:"defaultBandwidthBitsPerSecond"`
		// Reject the former manual-token input instead of silently ignoring it.
		AdmissionToken json.RawMessage `json:"admissionToken"`
	}
	if c.ShouldBindJSON(&in) != nil || len(in.AdmissionToken) != 0 {
		c.JSON(400, gin.H{"error": "invalid node settings"})
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, true) {
		return
	}
	s := *a.r.node.Load()
	if in.Name != nil {
		s.Name = strings.TrimSpace(*in.Name)
	}
	if in.PublicURL != nil {
		s.PublicURL = strings.TrimRight(strings.TrimSpace(*in.PublicURL), "/")
	}
	if in.AdmissionEnabled != nil {
		if !*in.AdmissionEnabled {
			s.AdmissionKey = ""
		} else if s.AdmissionKey == "" {
			key, err := randomSecret()
			if err != nil {
				apiUnavailable(c)
				return
			}
			s.AdmissionKey = key
		}
	}
	if in.DefaultBandwidth != nil {
		s.DefaultBandwidthBitsPerSecond = *in.DefaultBandwidth
	}
	if err := validateNodeSettings(s); err != nil {
		c.JSON(400, gin.H{"error": err.Error()})
		return
	}
	// Secure cookies must remain usable after changing the public address.
	if strings.HasPrefix(s.PublicURL, "https://") && !a.r.config.CookieSecure {
		c.JSON(400, gin.H{"error": "enable secure cookies before setting an HTTPS public URL"})
		return
	}
	a.saveNodeSettings(c, s, "relay.update")
}

func (a *Console) refreshRelayAdmissionToken(c *gin.Context) {
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, true) {
		return
	}
	key, err := randomSecret()
	if err != nil {
		apiUnavailable(c)
		return
	}
	s := *a.r.node.Load()
	s.AdmissionKey = key
	// Refresh changes only subsequent registrations, never existing device sockets.
	a.saveNodeSettings(c, s, "relay.token.refresh")
}

// Callers hold policyMu until both persistent and in-memory policy are updated.
func (a *Console) saveNodeSettings(c *gin.Context, s NodeSettings, action string) {
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := tx.Save(&s).Error; err != nil {
			return err
		}
		return audit(tx, currentUser(c).ID, action, "node")
	})
	if err != nil {
		apiUnavailable(c)
		return
	}
	a.r.node.Store(&s)
	c.JSON(200, nodeSettingsResponse(s))
}

var errInvalidOwner = errors.New("invalid or unavailable device owner")
var errOwnerQuota = errors.New("owner device quota reached")

func (a *Console) assignDeviceOwner(tx *gorm.DB, d *Device, raw json.RawMessage) error {
	if len(raw) == 0 {
		return nil
	}
	var id *uint
	if err := json.Unmarshal(raw, &id); err != nil {
		return errInvalidOwner
	}
	if id == nil {
		d.OwnerID = nil
		return nil
	}
	if *id == 0 {
		return errInvalidOwner
	}
	var u User
	if err := tx.First(&u, *id).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			return errInvalidOwner
		}
		return err
	}
	if u.Disabled {
		return errInvalidOwner
	}
	var count int64
	if err := tx.Model(&Device{}).Where("owner_id = ? AND id <> ?", *id, d.ID).Count(&count).Error; err != nil {
		return err
	}
	if count >= int64(u.MaxDevices) {
		return errOwnerQuota
	}
	d.OwnerID = id
	return nil
}
