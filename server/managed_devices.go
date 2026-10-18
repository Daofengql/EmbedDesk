// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"crypto/subtle"
	"errors"
	"gorm.io/gorm"
	"strings"
	"time"
)

var errPolicy = errors.New("access policy denied")

// The node-wide token is a simple registration filter. Device and account authorization are separate.
func (r *Relay) admissionAllowed(raw string) bool {
	configured := r.node.Load().AdmissionKey
	return configured == "" || subtle.ConstantTimeCompare([]byte(raw), []byte(configured)) == 1
}
func (r *Relay) devicePolicy(d Device) (int64, error) {
	if d.Disabled {
		return 0, errPolicy
	}
	if d.OwnerID == nil {
		return r.node.Load().DefaultBandwidthBitsPerSecond, nil
	}
	if r.console == nil {
		return 0, errPolicy
	}
	var u User
	if r.db.First(&u, *d.OwnerID).Error != nil || u.Disabled {
		return 0, errPolicy
	}
	return u.BandwidthBitsPerSecond, nil
}

// Registration proves possession of the stable random device credential, not account ownership.
// Administrators assign ownership separately; registration cannot transfer it.
func (r *Relay) registerDevice(digest string) (Device, int64, error) {
	d := Device{ID: strings.ToUpper(digest[:12]), CredentialHash: digest}
	bits := r.node.Load().DefaultBandwidthBitsPerSecond
	err := r.db.Transaction(func(tx *gorm.DB) error {
		err := tx.Where("credential_hash = ?", digest).First(&d).Error
		if err != nil && !errors.Is(err, gorm.ErrRecordNotFound) {
			return err
		}
		fresh := errors.Is(err, gorm.ErrRecordNotFound)
		if d.Disabled {
			return errPolicy
		}
		if d.OwnerID != nil {
			if r.console == nil {
				return errPolicy
			}
			var owner User
			if tx.First(&owner, *d.OwnerID).Error != nil || owner.Disabled {
				return errPolicy
			}
			bits = owner.BandwidthBitsPerSecond
		}
		now := time.Now().UTC()
		d.LastSeenAt = &now
		if fresh {
			return tx.Create(&d).Error
		}
		return tx.Model(&d).Update("last_seen_at", now).Error
	})
	return d, bits, err
}

func (r *Relay) disconnectWhere(match func(*peer) bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, p := range r.peers {
		if match(p) {
			p.stop()
		}
	}
}
