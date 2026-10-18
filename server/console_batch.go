// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"
	"gorm.io/gorm"
)

// Each batch explicitly names at most one page of resources. Never expand a search into a write.
func decodeBatch(c *gin.Context, in any) bool {
	c.Request.Body = http.MaxBytesReader(c.Writer, c.Request.Body, 16<<10)
	d := json.NewDecoder(c.Request.Body)
	d.DisallowUnknownFields()
	if d.Decode(in) != nil || d.Decode(new(any)) != io.EOF {
		c.JSON(400, gin.H{"error": "批量请求格式无效"})
		return false
	}
	return true
}
func batchIDs[T comparable](ids []T, valid func(T) bool) bool {
	if len(ids) == 0 || len(ids) > 100 {
		return false
	}
	seen := make(map[T]bool, len(ids))
	for _, id := range ids {
		if seen[id] || !valid(id) {
			return false
		}
		seen[id] = true
	}
	return true
}
func hexID(id string, length int) bool {
	return len(id) == length && !strings.ContainsFunc(id, func(ch rune) bool {
		return !(ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f' || ch >= 'A' && ch <= 'F')
	})
}

var errBatchMissing = errors.New("one or more batch resources not found")
var errLastAdmin = errors.New("at least one enabled administrator is required")

func batchFailed(c *gin.Context, err error) bool {
	if err == nil {
		return false
	}
	switch {
	case errors.Is(err, errBatchMissing):
		c.JSON(404, gin.H{"error": "部分选中记录不存在或无权管理，整批未执行，请刷新列表"})
	case errors.Is(err, errLastAdmin):
		c.JSON(409, gin.H{"error": "必须保留至少一个已启用的系统管理员，整批未执行"})
	case errors.Is(err, errInvalidOwner):
		c.JSON(400, gin.H{"error": "所属用户不存在或已禁用，整批未执行"})
	case errors.Is(err, errOwnerQuota):
		c.JSON(409, gin.H{"error": "分配后会超过用户设备上限，整批未执行"})
	default:
		apiUnavailable(c)
	}
	return true
}

func (a *Console) batchUsers(c *gin.Context) {
	var in struct {
		IDs        []uint `json:"ids"`
		Action     string `json:"action"`
		Bandwidth  *int64 `json:"bandwidthBitsPerSecond"`
		MaxDevices *int   `json:"maxDevices"`
	}
	if !decodeBatch(c, &in) {
		return
	}
	valid := batchIDs(in.IDs, func(id uint) bool { return id > 0 && uint64(id) <= 1<<32-1 })
	switch in.Action {
	case "enable", "disable":
		valid = valid && in.Bandwidth == nil && in.MaxDevices == nil
	case "bandwidth":
		valid = valid && in.Bandwidth != nil && *in.Bandwidth >= 8000 && *in.Bandwidth <= 100000000000 && in.MaxDevices == nil
	case "deviceLimit":
		valid = valid && in.MaxDevices != nil && *in.MaxDevices >= 1 && *in.MaxDevices <= 1024 && in.Bandwidth == nil
	default:
		valid = false
	}
	if !valid {
		c.JSON(400, gin.H{"error": "请选择 1–100 个不同用户，并提交有效的批量操作与配额"})
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, true) {
		return
	}
	disconnect := map[uint]bool{}
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		var users []User
		if err := tx.Where("id IN ?", in.IDs).Find(&users).Error; err != nil {
			return err
		}
		if len(users) != len(in.IDs) {
			return errBatchMissing
		}
		for _, u := range users {
			switch in.Action {
			case "enable", "disable":
				disabled := in.Action == "disable"
				if u.Disabled != disabled {
					u.SessionVersion++
				}
				u.Disabled = disabled
				disconnect[u.ID] = disabled
			case "bandwidth":
				disconnect[u.ID] = u.BandwidthBitsPerSecond != *in.Bandwidth
				u.BandwidthBitsPerSecond = *in.Bandwidth
			case "deviceLimit":
				u.MaxDevices = *in.MaxDevices
			}
			if err := tx.Save(&u).Error; err != nil {
				return err
			}
			if err := audit(tx, currentUser(c).ID, "user.batch."+in.Action, userTarget(u.ID)); err != nil {
				return err
			}
		}
		var enabled int64
		if err := tx.Model(&User{}).Where("role = ? AND disabled = ?", "admin", false).Count(&enabled).Error; err != nil {
			return err
		}
		if enabled == 0 {
			return errLastAdmin
		}
		return nil
	})
	if batchFailed(c, err) {
		return
	}
	a.r.disconnectWhere(func(p *peer) bool { return disconnect[p.ownerID] })
	c.JSON(200, gin.H{"affected": len(in.IDs)})
}

func (a *Console) batchDevices(c *gin.Context) {
	var in struct {
		IDs     []string        `json:"ids"`
		Action  string          `json:"action"`
		OwnerID json.RawMessage `json:"ownerId"`
	}
	if !decodeBatch(c, &in) {
		return
	}
	for i := range in.IDs {
		in.IDs[i] = strings.ToUpper(in.IDs[i])
	}
	valid := batchIDs(in.IDs, func(id string) bool { return hexID(id, 12) })
	switch in.Action {
	case "enable", "disable", "disconnect", "delete":
		valid = valid && len(in.OwnerID) == 0
	case "assign":
		var id *uint
		valid = valid && len(in.OwnerID) > 0 && json.Unmarshal(in.OwnerID, &id) == nil && (id == nil || *id > 0 && uint64(*id) <= 1<<32-1)
	default:
		valid = false
	}
	if !valid {
		c.JSON(400, gin.H{"error": "请选择 1–100 个不同设备，并提交有效的批量操作"})
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	if in.Action == "assign" && currentUser(c).Role != "admin" {
		c.JSON(403, gin.H{"error": "仅系统管理员可批量分配设备归属"})
		return
	}
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		var devices []Device
		if err := scope(tx, currentUser(c)).Where("id IN ?", in.IDs).Find(&devices).Error; err != nil {
			return err
		}
		if len(devices) != len(in.IDs) {
			return errBatchMissing
		}
		for _, d := range devices {
			switch in.Action {
			case "enable", "disable":
				d.Disabled = in.Action == "disable"
			case "assign":
				if err := a.assignDeviceOwner(tx, &d, in.OwnerID); err != nil {
					return err
				}
			case "delete":
				if err := tx.Delete(&d).Error; err != nil {
					return err
				}
			}
			if in.Action == "enable" || in.Action == "disable" || in.Action == "assign" {
				if err := tx.Save(&d).Error; err != nil {
					return err
				}
			}
			if err := audit(tx, currentUser(c).ID, "device.batch."+in.Action, d.ID); err != nil {
				return err
			}
		}
		return nil
	})
	if batchFailed(c, err) {
		return
	}
	if in.Action != "enable" {
		selected := make(map[string]bool, len(in.IDs))
		for _, id := range in.IDs {
			selected[id] = true
		}
		a.r.disconnectWhere(func(p *peer) bool { return selected[p.id] })
	}
	c.JSON(200, gin.H{"affected": len(in.IDs)})
}

func (a *Console) batchSessions(c *gin.Context) {
	var in struct {
		IDs    []string `json:"ids"`
		Action string   `json:"action"`
	}
	if !decodeBatch(c, &in) {
		return
	}
	if in.Action != "close" || !batchIDs(in.IDs, func(id string) bool { return hexID(id, 32) && id == strings.ToLower(id) }) {
		c.JSON(400, gin.H{"error": "请选择 1–100 个不同会话并使用关闭操作"})
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	if !a.mutation(c, false) {
		return
	}
	selected := make(map[string]*session, len(in.IDs))
	a.r.mu.Lock()
	for _, id := range in.IDs {
		if s := a.r.sessions[id]; s != nil {
			if !a.visibleSession(currentUser(c), s) {
				a.r.mu.Unlock()
				batchFailed(c, errBatchMissing)
				return
			}
			selected[id] = s
		}
	}
	a.r.mu.Unlock()
	err := a.r.db.Transaction(func(tx *gorm.DB) error {
		for id := range selected {
			if err := audit(tx, currentUser(c).ID, "session.batch.close", id); err != nil {
				return err
			}
		}
		return nil
	})
	if batchFailed(c, err) {
		return
	}
	affected := 0
	a.r.mu.Lock()
	for id, s := range selected {
		if a.r.sessions[id] != s {
			continue
		}
		delete(a.r.sessions, id)
		affected++
		for _, device := range []string{s.origin, s.owner} {
			if p := a.r.peers[device]; p != nil {
				p.send(Message{Type: "close", Session: id, Error: "session closed from console"})
			}
		}
	}
	a.r.mu.Unlock()
	c.JSON(200, gin.H{"affected": affected, "skipped": len(in.IDs) - affected})
}
