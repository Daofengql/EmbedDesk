// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/json"
	"errors"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/gin-gonic/gin"
	"golang.org/x/crypto/bcrypt"
	"gorm.io/gorm"
)

func userTarget(id uint) string { return strconv.FormatUint(uint64(id), 10) }
func audit(tx *gorm.DB, actor uint, action, target string) error {
	return tx.Create(&AuditEvent{ActorID: actor, Action: action, Target: target}).Error
}
func apiUnavailable(c *gin.Context) {
	c.JSON(503, gin.H{"error": "operation failed; storage unavailable or account changed"})
}
func validLabel(value string, required bool) bool {
	n := len([]rune(value))
	return (!required || n > 0) && n <= 80 && !strings.ContainsFunc(value, unicode.IsControl)
}
func resourceID(c *gin.Context) (uint, bool) {
	id, err := strconv.ParseUint(c.Param("id"), 10, 32)
	if err != nil || id == 0 {
		c.JSON(400, gin.H{"error": "invalid ID"})
		return 0, false
	}
	return uint(id), true
}

// Recheck after taking policyMu so a queued request cannot act with a revoked role/session.
func (a *Console) mutation(c *gin.Context, admin bool) bool {
	u := currentUser(c)
	var latest User
	if a.r.db.First(&latest, u.ID).Error != nil || latest.Disabled || latest.SessionVersion != u.SessionVersion {
		c.JSON(401, gin.H{"error": "account changed; sign in again"})
		return false
	}
	if admin && latest.Role != "admin" {
		c.JSON(403, gin.H{"error": "administrator required"})
		return false
	}
	if latest.Role == "guest" {
		c.JSON(403, gin.H{"error": "guest access is read-only"})
		return false
	}
	return true
}
func scope(db *gorm.DB, u User) *gorm.DB {
	if u.Role != "admin" {
		return db.Where("owner_id = ?", u.ID)
	}
	return db
}
func pagination(c *gin.Context) (int, int, string, bool) {
	p, err := strconv.Atoi(c.DefaultQuery("page", "1"))
	n, err2 := strconv.Atoi(c.DefaultQuery("pageSize", "20"))
	q := strings.TrimSpace(c.Query("q"))
	if err != nil || err2 != nil || p < 1 || p > 1000000 || n < 1 || n > 100 || len([]rune(q)) > 80 {
		c.JSON(400, gin.H{"error": "invalid pagination or search"})
		return 0, 0, "", false
	}
	return p, n, q, true
}
func listResult(c *gin.Context, items any, total int64, p, n int) {
	c.JSON(200, gin.H{"items": items, "total": total, "page": p, "pageSize": n})
}

func (a *Console) overview(c *gin.Context) {
	u := currentUser(c)
	var users, devices int64
	if u.Role == "admin" {
		if a.r.db.Model(&User{}).Count(&users).Error != nil {
			apiUnavailable(c)
			return
		}
	} else {
		users = 1
	}
	if scope(a.r.db.Model(&Device{}), u).Count(&devices).Error != nil {
		apiUnavailable(c)
		return
	}
	a.r.mu.Lock()
	online, sessions := 0, 0
	for _, p := range a.r.peers {
		if u.Role == "admin" || p.ownerID == u.ID {
			online++
		}
	}
	for _, s := range a.r.sessions {
		p, q := a.r.peers[s.origin], a.r.peers[s.owner]
		if u.Role == "admin" || (p != nil && p.ownerID == u.ID) || (q != nil && q.ownerID == u.ID) {
			sessions++
		}
	}
	a.r.mu.Unlock()
	response := gin.H{"users": users, "devices": devices, "onlineDevices": online, "sessions": sessions, "relay": gin.H{"databaseDriver": a.r.config.driver(), "redisEnabled": a.redis != nil, "admissionRequired": a.r.node.Load().AdmissionKey != "", "bandwidthBitsPerSecond": a.r.node.Load().DefaultBandwidthBitsPerSecond, "uptimeSeconds": int64(time.Since(a.r.startedAt).Seconds())}}
	if u.Role == "admin" {
		response["server"] = a.r.metrics.Load()
	}
	c.JSON(200, response)
}
func (a *Console) users(c *gin.Context) {
	p, n, q, ok := pagination(c)
	if !ok {
		return
	}
	db := a.r.db.Model(&User{})
	if q != "" {
		db = db.Where("username LIKE ? OR display_name LIKE ?", "%"+q+"%", "%"+q+"%")
	}
	var total int64
	items := []User{}
	if db.Count(&total).Error != nil || db.Order("id DESC").Offset((p-1)*n).Limit(n).Find(&items).Error != nil {
		apiUnavailable(c)
		return
	}
	listResult(c, items, total, p, n)
}
func (a *Console) createUser(c *gin.Context) {
	var in struct {
		Username    string `json:"username"`
		DisplayName string `json:"displayName"`
		Password    string `json:"password"`
		Role        string `json:"role"`
		MaxDevices  int    `json:"maxDevices"`
		Bandwidth   *int64 `json:"bandwidthBitsPerSecond"`
	}
	if c.ShouldBindJSON(&in) != nil {
		c.JSON(400, gin.H{"error": "invalid user"})
		return
	}
	u := User{Username: strings.ToLower(strings.TrimSpace(in.Username)), DisplayName: strings.TrimSpace(in.DisplayName), Role: in.Role, MaxDevices: in.MaxDevices, BandwidthBitsPerSecond: a.r.node.Load().DefaultBandwidthBitsPerSecond, SessionVersion: 1}
	if in.Bandwidth != nil {
		u.BandwidthBitsPerSecond = *in.Bandwidth
	}
	if !validUser(u) || !validLabel(u.DisplayName, true) || !validPassword(in.Password) {
		c.JSON(400, gin.H{"error": "invalid user: username 3-48 lowercase ASCII characters; password 12-72 bytes; device limit 1-1024; bandwidth 8000-100000000000"})
		return
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(in.Password), bcrypt.DefaultCost)
	if err != nil {
		apiUnavailable(c)
		return
	}
	u.PasswordHash = string(hash)
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, true) {
		return
	}
	if in.Bandwidth == nil {
		u.BandwidthBitsPerSecond = a.r.node.Load().DefaultBandwidthBitsPerSecond
	}
	var existing int64
	if a.r.db.Model(&User{}).Where("username = ?", u.Username).Count(&existing).Error != nil {
		apiUnavailable(c)
		return
	}
	if existing > 0 {
		c.JSON(409, gin.H{"error": "username already exists"})
		return
	}
	err = a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(&u).Error; err != nil {
			return err
		}
		return audit(tx, currentUser(c).ID, "user.create", userTarget(u.ID))
	})
	if err != nil {
		apiUnavailable(c)
		return
	}
	c.JSON(201, u)
}
func (a *Console) updateUser(c *gin.Context) {
	id, ok := resourceID(c)
	if !ok {
		return
	}
	var in struct {
		DisplayName *string `json:"displayName"`
		Role        *string `json:"role"`
		Disabled    *bool   `json:"disabled"`
		MaxDevices  *int    `json:"maxDevices"`
		Bandwidth   *int64  `json:"bandwidthBitsPerSecond"`
		Password    *string `json:"password"`
	}
	if c.ShouldBindJSON(&in) != nil {
		c.JSON(400, gin.H{"error": "invalid user"})
		return
	}
	var hash []byte
	if in.Password != nil {
		if !validPassword(*in.Password) {
			c.JSON(400, gin.H{"error": "password must contain 12-72 bytes"})
			return
		}
		var err error
		hash, err = bcrypt.GenerateFromPassword([]byte(*in.Password), bcrypt.DefaultCost)
		if err != nil {
			apiUnavailable(c)
			return
		}
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, true) {
		return
	}
	var u User
	if a.r.db.First(&u, id).Error != nil {
		c.JSON(404, gin.H{"error": "user not found"})
		return
	}
	old := u
	if in.DisplayName != nil {
		u.DisplayName = strings.TrimSpace(*in.DisplayName)
	}
	if in.Role != nil {
		u.Role = *in.Role
	}
	if in.Disabled != nil {
		u.Disabled = *in.Disabled
	}
	if in.MaxDevices != nil {
		u.MaxDevices = *in.MaxDevices
	}
	if in.Bandwidth != nil {
		u.BandwidthBitsPerSecond = *in.Bandwidth
	}
	if !validUser(u) || !validLabel(u.DisplayName, true) {
		c.JSON(400, gin.H{"error": "invalid user settings"})
		return
	}
	if old.Role == "admin" && !old.Disabled && (u.Role != "admin" || u.Disabled) {
		var count int64
		if a.r.db.Model(&User{}).Where("role = ? AND disabled = ?", "admin", false).Count(&count).Error != nil {
			apiUnavailable(c)
			return
		}
		if count <= 1 {
			c.JSON(409, gin.H{"error": "at least one enabled administrator is required"})
			return
		}
	}
	if in.Password != nil {
		u.PasswordHash = string(hash)
	}
	if in.Password != nil || old.Role != u.Role || old.Disabled != u.Disabled {
		u.SessionVersion++
	}
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := tx.Save(&u).Error; err != nil {
			return err
		}
		return audit(tx, currentUser(c).ID, "user.update", userTarget(id))
	})
	if err != nil {
		apiUnavailable(c)
		return
	}
	if u.Disabled || old.BandwidthBitsPerSecond != u.BandwidthBitsPerSecond {
		a.r.disconnectWhere(func(p *peer) bool { return p.ownerID == id })
	}
	c.JSON(200, u)
}

type deviceView struct {
	ID         string     `json:"id"`
	OwnerID    *uint      `json:"ownerId"`
	OwnerName  string     `json:"ownerName"`
	Label      string     `json:"label"`
	Disabled   bool       `json:"disabled"`
	Online     bool       `json:"online"`
	CreatedAt  time.Time  `json:"createdAt"`
	LastSeenAt *time.Time `json:"lastSeenAt"`
}

func (a *Console) ownerNames(ids []uint) (map[uint]string, error) {
	names := map[uint]string{}
	if len(ids) == 0 {
		return names, nil
	}
	var users []User
	if err := a.r.db.Select("id", "username").Where("id IN ?", ids).Find(&users).Error; err != nil {
		return nil, err
	}
	for _, u := range users {
		names[u.ID] = u.Username
	}
	return names, nil
}
func (a *Console) devices(c *gin.Context) {
	p, n, q, ok := pagination(c)
	if !ok {
		return
	}
	db := scope(a.r.db.Model(&Device{}), currentUser(c))
	if q != "" {
		db = db.Where("(id LIKE ? OR label LIKE ?)", "%"+q+"%", "%"+q+"%")
	}
	var total int64
	var devices []Device
	if db.Count(&total).Error != nil || db.Order("created_at DESC, id DESC").Offset((p-1)*n).Limit(n).Find(&devices).Error != nil {
		apiUnavailable(c)
		return
	}
	ids := []uint{}
	for _, d := range devices {
		if d.OwnerID != nil {
			ids = append(ids, *d.OwnerID)
		}
	}
	names, err := a.ownerNames(ids)
	if err != nil {
		apiUnavailable(c)
		return
	}
	items := []deviceView{}
	a.r.mu.Lock()
	for _, d := range devices {
		v := deviceView{ID: d.ID, OwnerID: d.OwnerID, Label: d.Label, Disabled: d.Disabled, Online: a.r.peers[d.ID] != nil, CreatedAt: d.CreatedAt, LastSeenAt: d.LastSeenAt}
		if d.OwnerID != nil {
			v.OwnerName = names[*d.OwnerID]
		}
		items = append(items, v)
	}
	a.r.mu.Unlock()
	listResult(c, items, total, p, n)
}
func (a *Console) ownedDevice(c *gin.Context) (Device, bool) {
	var d Device
	err := scope(a.r.db, currentUser(c)).Where("id = ?", c.Param("id")).First(&d).Error
	if err != nil {
		c.JSON(404, gin.H{"error": "device not found"})
		return d, false
	}
	return d, true
}

// A device may be claimed by ID only while anonymous. Transferring ownership is administrative.
func (a *Console) bindDevice(c *gin.Context) {
	var in struct {
		DeviceID string `json:"deviceId"`
	}
	if c.ShouldBindJSON(&in) != nil {
		c.JSON(400, gin.H{"error": "invalid device ID"})
		return
	}
	id := strings.ToUpper(strings.TrimSpace(in.DeviceID))
	if len(id) != 12 || strings.ContainsFunc(id, func(ch rune) bool { return !(ch >= '0' && ch <= '9' || ch >= 'A' && ch <= 'F') }) {
		c.JSON(400, gin.H{"error": "device ID must be 12 hexadecimal characters"})
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	u := currentUser(c)
	var d Device
	if err := a.r.db.First(&d, "id = ?", id).Error; err != nil {
		if errors.Is(err, gorm.ErrRecordNotFound) {
			c.JSON(404, gin.H{"error": "device not found"})
		} else {
			apiUnavailable(c)
		}
		return
	}
	if d.OwnerID != nil {
		if *d.OwnerID == u.ID {
			c.JSON(200, gin.H{"ok": true})
			return
		}
		c.JSON(409, gin.H{"error": "device is already bound to another account"})
		return
	}
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := a.assignDeviceOwner(tx, &d, json.RawMessage(userTarget(u.ID))); err != nil {
			return err
		}
		result := tx.Model(&Device{}).Where("id = ? AND owner_id IS NULL", id).Update("owner_id", u.ID)
		if result.Error != nil {
			return result.Error
		}
		if result.RowsAffected != 1 {
			return errPolicy
		}
		return audit(tx, u.ID, "device.bind", id)
	})
	if errors.Is(err, errOwnerQuota) || errors.Is(err, errPolicy) {
		c.JSON(409, gin.H{"error": "device already bound or device quota reached"})
		return
	}
	if err != nil {
		apiUnavailable(c)
		return
	}
	a.r.disconnectWhere(func(p *peer) bool { return p.id == id })
	c.JSON(200, gin.H{"ok": true})
}
func (a *Console) updateDevice(c *gin.Context) {
	var in struct {
		Label    *string         `json:"label"`
		Disabled *bool           `json:"disabled"`
		OwnerID  json.RawMessage `json:"ownerId"`
	}
	if c.ShouldBindJSON(&in) != nil || (in.Label != nil && !validLabel(strings.TrimSpace(*in.Label), false)) {
		c.JSON(400, gin.H{"error": "invalid device settings"})
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	if len(in.OwnerID) > 0 && currentUser(c).Role != "admin" {
		c.JSON(403, gin.H{"error": "only administrators can assign device ownership"})
		return
	}
	d, ok := a.ownedDevice(c)
	if !ok {
		return
	}
	if in.Label != nil {
		d.Label = strings.TrimSpace(*in.Label)
	}
	if in.Disabled != nil {
		d.Disabled = *in.Disabled
	}
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := a.assignDeviceOwner(tx, &d, in.OwnerID); err != nil {
			return err
		}
		if err := tx.Save(&d).Error; err != nil {
			return err
		}
		return audit(tx, currentUser(c).ID, "device.update", d.ID)
	})
	if err != nil {
		if errors.Is(err, errInvalidOwner) {
			c.JSON(400, gin.H{"error": err.Error()})
			return
		}
		if errors.Is(err, errOwnerQuota) {
			c.JSON(409, gin.H{"error": err.Error()})
			return
		}
		apiUnavailable(c)
		return
	}
	if d.Disabled || len(in.OwnerID) > 0 {
		a.r.disconnectWhere(func(p *peer) bool { return p.id == d.ID })
	}
	c.JSON(200, gin.H{"ok": true})
}
func (a *Console) disconnectDevice(c *gin.Context) {
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	d, ok := a.ownedDevice(c)
	if !ok {
		return
	}
	if audit(a.r.db, currentUser(c).ID, "device.disconnect", d.ID) != nil {
		apiUnavailable(c)
		return
	}
	a.r.disconnectWhere(func(p *peer) bool { return p.id == d.ID })
	c.JSON(200, gin.H{"ok": true})
}
func (a *Console) deleteDevice(c *gin.Context) {
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	d, ok := a.ownedDevice(c)
	if !ok {
		return
	}
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := tx.Delete(&d).Error; err != nil {
			return err
		}
		return audit(tx, currentUser(c).ID, "device.delete", d.ID)
	})
	if err != nil {
		apiUnavailable(c)
		return
	}
	a.r.disconnectWhere(func(p *peer) bool { return p.id == d.ID })
	c.JSON(200, gin.H{"ok": true})
}

type auditView struct {
	AuditEvent
	ActorName string `json:"actorName"`
}

func (a *Console) auditEvents(c *gin.Context) {
	p, n, q, ok := pagination(c)
	if !ok {
		return
	}
	db := a.r.db.Model(&AuditEvent{})
	if q != "" {
		db = db.Where("(action LIKE ? OR target LIKE ? OR actor_id IN (SELECT id FROM users WHERE username LIKE ? OR display_name LIKE ?))", "%"+q+"%", "%"+q+"%", "%"+q+"%", "%"+q+"%")
	}
	var total int64
	var events []AuditEvent
	if db.Count(&total).Error != nil || db.Order("id DESC").Offset((p-1)*n).Limit(n).Find(&events).Error != nil {
		apiUnavailable(c)
		return
	}
	ids := []uint{}
	for _, e := range events {
		ids = append(ids, e.ActorID)
	}
	names, err := a.ownerNames(ids)
	if err != nil {
		apiUnavailable(c)
		return
	}
	items := []auditView{}
	for _, e := range events {
		items = append(items, auditView{e, names[e.ActorID]})
	}
	listResult(c, items, total, p, n)
}

type sessionView struct {
	ID             string    `json:"id"`
	OriginDeviceID string    `json:"originDeviceId"`
	TargetDeviceID string    `json:"targetDeviceId"`
	Accepted       bool      `json:"accepted"`
	Stage          int       `json:"stage"`
	CreatedAt      time.Time `json:"createdAt"`
	ExpiresAt      time.Time `json:"expiresAt"`
}

func (a *Console) visibleSession(u User, s *session) bool {
	if u.Role == "admin" {
		return true
	}
	p, q := a.r.peers[s.origin], a.r.peers[s.owner]
	return (p != nil && p.ownerID == u.ID) || (q != nil && q.ownerID == u.ID)
}
func (a *Console) activeSessions(c *gin.Context) {
	p, n, q, ok := pagination(c)
	if !ok {
		return
	}
	u := currentUser(c)
	items := []sessionView{}
	a.r.mu.Lock()
	for id, s := range a.r.sessions {
		if !a.visibleSession(u, s) || (q != "" && !strings.Contains(id, q) && !strings.Contains(s.origin, strings.ToUpper(q)) && !strings.Contains(s.owner, strings.ToUpper(q))) {
			continue
		}
		items = append(items, sessionView{id, s.origin, s.owner, s.accepted, s.stage, s.createdAt, s.expires})
	}
	a.r.mu.Unlock()
	sort.Slice(items, func(i, j int) bool {
		if items[i].CreatedAt.Equal(items[j].CreatedAt) {
			return items[i].ID > items[j].ID
		}
		return items[i].CreatedAt.After(items[j].CreatedAt)
	})
	total := len(items)
	start := (p - 1) * n
	if start > total {
		start = total
	}
	end := start + n
	if end > total {
		end = total
	}
	listResult(c, items[start:end], int64(total), p, n)
}
func (a *Console) closeActiveSession(c *gin.Context) {
	id := c.Param("id")
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	a.r.mu.Lock()
	s := a.r.sessions[id]
	visible := s != nil && a.visibleSession(currentUser(c), s)
	a.r.mu.Unlock()
	if !visible {
		c.JSON(404, gin.H{"error": "session not found"})
		return
	}
	if audit(a.r.db, currentUser(c).ID, "session.close", id) != nil {
		apiUnavailable(c)
		return
	}
	a.r.mu.Lock()
	if a.r.sessions[id] == s {
		delete(a.r.sessions, id)
		for _, device := range []string{s.origin, s.owner} {
			if p := a.r.peers[device]; p != nil {
				p.send(Message{Type: "close", Session: id, Error: "session closed from console"})
			}
		}
	}
	a.r.mu.Unlock()
	c.JSON(200, gin.H{"ok": true})
}
