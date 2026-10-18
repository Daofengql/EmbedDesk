// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"math/big"
	"time"

	"github.com/dchest/captcha"
	"github.com/gin-gonic/gin"
	"github.com/redis/go-redis/v9"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

const captchaTTL = 3 * time.Minute

type CaptchaChallenge struct {
	ID         string    `gorm:"primaryKey;size:64"`
	AnswerHash string    `gorm:"size:64;not null"`
	ExpiresAt  time.Time `gorm:"index"`
}

func (a *Console) putCaptcha(ctx context.Context, id, answer string) error {
	hash := hashCredential(id + ":" + answer)
	if a.redis != nil {
		return a.redis.Set(ctx, a.prefix+"captcha:"+hashCredential(id), hash, captchaTTL).Err()
	}
	if err := a.r.db.WithContext(ctx).Where("expires_at <= ?", time.Now().UTC()).Delete(&CaptchaChallenge{}).Error; err != nil {
		return err
	}
	return a.r.db.WithContext(ctx).Create(&CaptchaChallenge{ID: hashCredential(id), AnswerHash: hash, ExpiresAt: time.Now().UTC().Add(captchaTTL)}).Error
}
func (a *Console) consumeCaptcha(ctx context.Context, id, answer string) (bool, error) {
	if len(id) != 43 {
		return false, nil
	}
	var expected string
	if a.redis != nil {
		const script = `local value=redis.call('GET',KEYS[1]); if value then redis.call('DEL',KEYS[1]) end; return value`
		value, err := a.redis.Eval(ctx, script, []string{a.prefix + "captcha:" + hashCredential(id)}).Text()
		if errors.Is(err, redis.Nil) {
			return false, nil
		}
		if err != nil {
			return false, err
		}
		expected = value
	} else {
		err := a.r.db.WithContext(ctx).Transaction(func(tx *gorm.DB) error {
			var stored CaptchaChallenge
			if err := tx.Clauses(clause.Locking{Strength: "UPDATE"}).First(&stored, "id = ?", hashCredential(id)).Error; err != nil {
				return err
			}
			if err := tx.Delete(&stored).Error; err != nil {
				return err
			}
			if time.Now().Before(stored.ExpiresAt) {
				expected = stored.AnswerHash
			}
			return nil
		})
		if errors.Is(err, gorm.ErrRecordNotFound) {
			return false, nil
		}
		if err != nil {
			return false, err
		}
	}
	if len(answer) != 6 {
		return false, nil
	}
	for _, digit := range answer {
		if digit < '0' || digit > '9' {
			return false, nil
		}
	}
	return expected != "" && subtle.ConstantTimeCompare([]byte(expected), []byte(hashCredential(id+":"+answer))) == 1, nil
}
func (a *Console) captchaImage(c *gin.Context) {
	if !a.r.config.RegistrationEnabled {
		c.JSON(403, gin.H{"error": "registration is disabled"})
		return
	}
	if !a.captchaBudget.Allow() {
		c.Header("Retry-After", "1")
		c.JSON(429, gin.H{"error": "captcha requests too frequent"})
		return
	}
	id, err := randomSecret()
	if err != nil {
		apiUnavailable(c)
		return
	}
	digits := make([]byte, 6)
	answer := make([]byte, 6)
	for i := range digits {
		n, err := rand.Int(rand.Reader, big.NewInt(10))
		if err != nil {
			apiUnavailable(c)
			return
		}
		digits[i] = byte(n.Int64())
		answer[i] = '0' + digits[i]
	}
	var image bytes.Buffer
	if _, err := captcha.NewImage(id, digits, 240, 80).WriteTo(&image); err != nil {
		apiUnavailable(c)
		return
	}
	if err := a.putCaptcha(c.Request.Context(), id, string(answer)); err != nil {
		apiUnavailable(c)
		return
	}
	c.JSON(200, gin.H{"id": id, "image": "data:image/png;base64," + base64.StdEncoding.EncodeToString(image.Bytes()), "expiresIn": int(captchaTTL.Seconds())})
}
