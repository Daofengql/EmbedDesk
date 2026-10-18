// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"embed"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io/fs"
	"net/http"
	"net/url"
	"path"
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/gin-gonic/gin"
	"github.com/redis/go-redis/v9"
	"golang.org/x/crypto/bcrypt"
	"golang.org/x/time/rate"
	"gorm.io/gorm"
)

const consoleCookie = "embeddesk_console"
const consoleSessionTTL = 12 * time.Hour

// A tracked placeholder allows protocol-only builds; release builds run npm build first.
//
//go:embed all:www/dist
var consoleAssets embed.FS

type User struct {
	ID                     uint      `json:"id"`
	Username               string    `json:"username" gorm:"uniqueIndex;size:48;not null"`
	DisplayName            string    `json:"displayName" gorm:"size:80;not null"`
	PasswordHash           string    `json:"-" gorm:"size:100;not null"`
	Role                   string    `json:"role" gorm:"size:16;not null"`
	Disabled               bool      `json:"disabled"`
	MaxDevices             int       `json:"maxDevices"`
	BandwidthBitsPerSecond int64     `json:"bandwidthBitsPerSecond"`
	SessionVersion         uint      `json:"-"`
	CreatedAt              time.Time `json:"createdAt"`
}

type ConsoleSession struct {
	ID        string `gorm:"primaryKey;size:64"`
	UserID    uint   `gorm:"index"`
	Version   uint
	CSRF      string    `gorm:"size:48"`
	ExpiresAt time.Time `gorm:"index"`
}

type AuditEvent struct {
	ID        uint      `json:"id"`
	ActorID   uint      `json:"actorId" gorm:"index"`
	Action    string    `json:"action" gorm:"size:48;not null"`
	Target    string    `json:"target" gorm:"size:64;not null"`
	CreatedAt time.Time `json:"createdAt" gorm:"index"`
}

type loginWindow struct {
	count   int
	expires time.Time
}
type Console struct {
	r             *Relay
	redis         *redis.Client
	prefix        string
	loginMu       sync.Mutex
	loginWindows  map[string]loginWindow
	loginBudget   *rate.Limiter
	captchaBudget *rate.Limiter
	dummyHash     []byte
	stop          chan struct{}
	done          chan struct{}
}

func randomSecret() (string, error) {
	var b [32]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b[:]), nil
}

var usernamePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9_.-]{2,47}$`)

func validPassword(password string) bool { return len(password) >= 12 && len(password) <= 72 }
func validUser(u User) bool {
	return usernamePattern.MatchString(u.Username) && len([]rune(u.DisplayName)) >= 1 && len([]rune(u.DisplayName)) <= 80 &&
		(u.Role == "admin" || u.Role == "user" || u.Role == "guest") && u.MaxDevices >= 1 && u.MaxDevices <= 1024 &&
		u.BandwidthBitsPerSecond >= minBandwidthBitsPerSecond && u.BandwidthBitsPerSecond <= maxBandwidthBitsPerSecond
}

func newConsole(r *Relay) (*Console, error) {
	if err := r.db.AutoMigrate(&User{}, &ConsoleSession{}, &AuditEvent{}, &CaptchaChallenge{}, &NodeSettings{}); err != nil {
		return nil, err
	}
	if err := r.loadNodeSettings(); err != nil {
		return nil, err
	}
	a := &Console{r: r, prefix: r.config.RedisPrefix, loginWindows: make(map[string]loginWindow), loginBudget: rate.NewLimiter(5, 10), captchaBudget: rate.NewLimiter(2, 10), stop: make(chan struct{}), done: make(chan struct{})}
	if a.prefix == "" {
		a.prefix = "embeddesk:"
	}
	if r.config.RedisURL != "" {
		opts, err := redis.ParseURL(r.config.RedisURL)
		if err != nil {
			return nil, errors.New("invalid Redis configuration")
		}
		opts.DialTimeout, opts.ReadTimeout, opts.WriteTimeout = 3*time.Second, 2*time.Second, 2*time.Second
		opts.MaxRetries = 0
		opts.PoolSize = 8
		a.redis = redis.NewClient(opts)
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		err = a.redis.Ping(ctx).Err()
		cancel()
		if err != nil {
			_ = a.redis.Close()
			return nil, errors.New("Redis connection failed")
		}
	}
	fail := func(err error) (*Console, error) {
		if a.redis != nil {
			_ = a.redis.Close()
		}
		return nil, err
	}
	a.dummyHash, _ = bcrypt.GenerateFromPassword([]byte("invalid-account-placeholder"), bcrypt.DefaultCost)
	if r.config.BootstrapUsername != "" {
		var count int64
		if err := r.db.Model(&User{}).Count(&count).Error; err != nil {
			return fail(err)
		}
		// Bootstrap only an empty database; never reset a password on restart.
		if count == 0 {
			u := User{Username: strings.ToLower(strings.TrimSpace(r.config.BootstrapUsername)), DisplayName: "Administrator", Role: "admin", MaxDevices: 100, BandwidthBitsPerSecond: r.node.Load().DefaultBandwidthBitsPerSecond, SessionVersion: 1}
			if !validUser(u) || !validPassword(r.config.BootstrapPassword) {
				return fail(errors.New("invalid bootstrap username or password (12-72 bytes required)"))
			}
			hash, err := bcrypt.GenerateFromPassword([]byte(r.config.BootstrapPassword), bcrypt.DefaultCost)
			if err != nil {
				return fail(err)
			}
			u.PasswordHash = string(hash)
			if err := r.db.Transaction(func(tx *gorm.DB) error {
				if err := tx.Create(&u).Error; err != nil {
					return err
				}
				return audit(tx, u.ID, "user.bootstrap", userTarget(u.ID))
			}); err != nil {
				return fail(err)
			}
		}
	}
	var administrators int64
	if err := r.db.Model(&User{}).Where("role = ? AND disabled = ?", "admin", false).Count(&administrators).Error; err != nil {
		return fail(err)
	}
	if administrators == 0 {
		return fail(errors.New("console requires an administrator; set bootstrap environment variables for an empty database"))
	}
	// Keep fallback sessions bounded even when nobody logs in again.
	go func() {
		defer close(a.done)
		ticker := time.NewTicker(10 * time.Minute)
		defer ticker.Stop()
		for {
			select {
			case <-a.stop:
				return
			case <-ticker.C:
				r.db.Where("expires_at < ?", time.Now().UTC()).Delete(&ConsoleSession{})
				r.db.Where("expires_at < ?", time.Now().UTC()).Delete(&CaptchaChallenge{})
			}
		}
	}()
	return a, nil
}

func (a *Console) close() {
	close(a.stop)
	<-a.done
	if a.redis != nil {
		_ = a.redis.Close()
	}
}

func (a *Console) routes(g *gin.Engine) {
	g.Use(func(c *gin.Context) {
		c.Header("X-Content-Type-Options", "nosniff")
		c.Header("X-Frame-Options", "DENY")
		c.Header("Referrer-Policy", "no-referrer")
		c.Next()
	})
	api := g.Group("/api/console")
	api.Use(func(c *gin.Context) {
		c.Header("Cache-Control", "no-store")
		if c.Request.Method != "GET" && c.Request.Method != "HEAD" {
			if c.ContentType() != "application/json" {
				c.AbortWithStatusJSON(415, gin.H{"error": "application/json required"})
				return
			}
			if origin := c.GetHeader("Origin"); origin != "" {
				u, err := url.Parse(origin)
				if err != nil || u.Host != c.Request.Host || (u.Scheme != "http" && u.Scheme != "https") || (a.r.node.Load().PublicURL != "" && strings.TrimRight(origin, "/") != strings.TrimRight(a.r.node.Load().PublicURL, "/")) {
					c.AbortWithStatusJSON(403, gin.H{"error": "cross-origin request denied"})
					return
				}
			}
		}
		c.Next()
	})
	api.POST("/auth/login", a.login)
	api.GET("/auth/options", func(c *gin.Context) { c.JSON(200, gin.H{"registrationEnabled": a.r.config.RegistrationEnabled}) })
	api.POST("/auth/register", a.registerUser)
	api.GET("/auth/captcha", a.captchaImage)
	api.Use(a.requireSession)
	api.GET("/auth/me", func(c *gin.Context) {
		c.JSON(200, gin.H{"user": currentUser(c), "csrfToken": c.MustGet("session").(ConsoleSession).CSRF})
	})
	api.POST("/auth/logout", a.logout)
	api.POST("/auth/password", a.password)
	api.GET("/overview", a.overview)
	api.GET("/sessions", a.activeSessions)
	api.POST("/sessions/batch", a.batchSessions)
	api.POST("/sessions/:id/close", a.closeActiveSession)
	api.GET("/devices", a.devices)
	api.POST("/devices/bind", a.bindDevice)
	api.POST("/devices/batch", a.batchDevices)
	api.PATCH("/devices/:id", a.updateDevice)
	api.POST("/devices/:id/disconnect", a.disconnectDevice)
	api.DELETE("/devices/:id", a.deleteDevice)
	api.GET("/relay", a.relaySettings)
	api.PATCH("/relay", a.adminOnly, a.updateRelaySettings)
	api.POST("/relay/admission-token/refresh", a.adminOnly, a.refreshRelayAdmissionToken)
	api.GET("/users", a.adminOnly, a.users)
	api.POST("/users", a.adminOnly, a.createUser)
	api.POST("/users/batch", a.adminOnly, a.batchUsers)
	api.PATCH("/users/:id", a.adminOnly, a.updateUser)
	api.GET("/audit", a.adminOnly, a.auditEvents)
	g.GET("/", func(c *gin.Context) { c.Redirect(302, "/console/") })
	g.GET("/console", func(c *gin.Context) { c.Redirect(302, "/console/") })
	g.GET("/console/*asset", a.static)
	g.HEAD("/console/*asset", a.static)
}

func (a *Console) static(c *gin.Context) {
	if (c.Request.Method != "GET" && c.Request.Method != "HEAD") || !strings.HasPrefix(c.Request.URL.Path, "/console/") {
		c.Status(404)
		return
	}
	files, _ := fs.Sub(consoleAssets, "www/dist")
	name := strings.TrimPrefix(c.Request.URL.Path, "/console/")
	if name == "" {
		name = "index.html"
	}
	if !fs.ValidPath(name) {
		c.Status(404)
		return
	}
	info, err := fs.Stat(files, name)
	if err != nil || !info.Mode().IsRegular() {
		if path.Ext(name) != "" {
			c.Status(404)
			return
		}
		name = "index.html"
	}
	if _, err := fs.Stat(files, name); err != nil {
		c.String(503, "Console assets unavailable. Run npm ci and npm run build in server/www, then rebuild the server.")
		return
	}
	c.Header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'")
	if name == "index.html" {
		c.Header("Cache-Control", "no-cache")
	} else {
		c.Header("Cache-Control", "public, max-age=31536000, immutable")
	}
	data, err := fs.ReadFile(files, name)
	if err != nil {
		c.Status(404)
		return
	}
	http.ServeContent(c.Writer, c.Request, name, time.Time{}, bytes.NewReader(data))
}

func (a *Console) loginAllowed(ctx context.Context, ip, username string) (bool, error) {
	if !a.loginBudget.Allow() {
		return false, nil
	}
	keys := []string{"ip:" + hashCredential(ip), "user:" + hashCredential(username)}
	if a.redis != nil {
		const script = `local n=redis.call('INCR',KEYS[1]); if n==1 then redis.call('EXPIRE',KEYS[1],900) end; return n`
		for i, key := range keys {
			n, err := a.redis.Eval(ctx, script, []string{a.prefix + "login:" + key}).Int64()
			if err != nil {
				return false, err
			}
			limit := int64(10)
			if i == 0 {
				limit = 100
			}
			if n > limit {
				return false, nil
			}
		}
		return true, nil
	}
	a.loginMu.Lock()
	defer a.loginMu.Unlock()
	now := time.Now()
	for k, v := range a.loginWindows {
		if now.After(v.expires) {
			delete(a.loginWindows, k)
		}
	}
	if len(a.loginWindows) >= 4096 {
		return false, nil
	}
	for i, key := range keys {
		w := a.loginWindows[key]
		if w.expires.IsZero() {
			w.expires = now.Add(15 * time.Minute)
		}
		w.count++
		a.loginWindows[key] = w
		limit := 10
		if i == 0 {
			limit = 100
		}
		if w.count > limit {
			return false, nil
		}
	}
	return true, nil
}

func (a *Console) saveSession(ctx context.Context, s ConsoleSession) error {
	if a.redis != nil {
		data, _ := json.Marshal(s)
		// A bounded sorted set tracks at most 20 active sessions per user.
		const script = `redis.call('ZREMRANGEBYSCORE',KEYS[2],'-inf',ARGV[3]); redis.call('SET',KEYS[1],ARGV[1],'EX',ARGV[2]); redis.call('ZADD',KEYS[2],ARGV[4],KEYS[1]); redis.call('EXPIRE',KEYS[2],ARGV[2]); while redis.call('ZCARD',KEYS[2])>20 do local old=redis.call('ZPOPMIN',KEYS[2],1); redis.call('DEL',old[1]); end; return 1`
		return a.redis.Eval(ctx, script, []string{a.prefix + "session:" + s.ID, a.prefix + "sessions:" + userTarget(s.UserID)}, string(data), int(consoleSessionTTL.Seconds()), time.Now().Unix(), s.ExpiresAt.Unix()).Err()
	}
	return a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := tx.Where("expires_at < ?", time.Now().UTC()).Delete(&ConsoleSession{}).Error; err != nil {
			return err
		}
		var sessions []ConsoleSession
		if err := tx.Where("user_id = ?", s.UserID).Order("expires_at DESC").Find(&sessions).Error; err != nil {
			return err
		}
		for i := 19; i < len(sessions); i++ {
			if err := tx.Delete(&sessions[i]).Error; err != nil {
				return err
			}
		}
		return tx.Create(&s).Error
	})
}
func (a *Console) readSession(ctx context.Context, id string) (ConsoleSession, error) {
	var s ConsoleSession
	if a.redis != nil {
		data, err := a.redis.Get(ctx, a.prefix+"session:"+id).Bytes()
		if err != nil {
			return s, err
		}
		err = json.Unmarshal(data, &s)
		return s, err
	}
	err := a.r.db.First(&s, "id = ?", id).Error
	return s, err
}
func (a *Console) deleteSession(ctx context.Context, s ConsoleSession) error {
	if a.redis != nil {
		_, err := a.redis.TxPipelined(ctx, func(p redis.Pipeliner) error {
			p.Del(ctx, a.prefix+"session:"+s.ID)
			p.ZRem(ctx, a.prefix+"sessions:"+userTarget(s.UserID), a.prefix+"session:"+s.ID)
			return nil
		})
		return err
	}
	return a.r.db.Delete(&s).Error
}
func (a *Console) cookie(c *gin.Context, value string, age int) {
	c.SetSameSite(http.SameSiteStrictMode)
	c.SetCookie(consoleCookie, value, age, "/api/console", "", a.r.config.CookieSecure, true)
}
func currentUser(c *gin.Context) User { return c.MustGet("user").(User) }
func (a *Console) requireSession(c *gin.Context) {
	raw, err := c.Cookie(consoleCookie)
	if err != nil || len(raw) != 43 {
		c.AbortWithStatusJSON(401, gin.H{"error": "login required"})
		return
	}
	s, err := a.readSession(c.Request.Context(), hashCredential(raw))
	if err != nil {
		if errors.Is(err, redis.Nil) || errors.Is(err, gorm.ErrRecordNotFound) {
			c.AbortWithStatusJSON(401, gin.H{"error": "session expired"})
		} else {
			c.AbortWithStatusJSON(503, gin.H{"error": "session storage unavailable"})
		}
		return
	}
	var u User
	if !time.Now().Before(s.ExpiresAt) || a.r.db.First(&u, s.UserID).Error != nil || u.Disabled || u.SessionVersion != s.Version {
		c.AbortWithStatusJSON(401, gin.H{"error": "session expired"})
		return
	}
	if c.Request.Method != "GET" && c.Request.Method != "HEAD" && subtle.ConstantTimeCompare([]byte(c.GetHeader("X-CSRF-Token")), []byte(s.CSRF)) != 1 {
		c.AbortWithStatusJSON(403, gin.H{"error": "invalid CSRF token"})
		return
	}
	c.Set("user", u)
	c.Set("session", s)
	c.Next()
}
func (a *Console) adminOnly(c *gin.Context) {
	if currentUser(c).Role != "admin" {
		c.AbortWithStatusJSON(403, gin.H{"error": "administrator required"})
		return
	}
	c.Next()
}

func (a *Console) registerUser(c *gin.Context) {
	if !a.r.config.RegistrationEnabled {
		c.JSON(403, gin.H{"error": "registration is disabled"})
		return
	}
	var in struct {
		Username    string `json:"username"`
		DisplayName string `json:"displayName"`
		Password    string `json:"password"`
		CaptchaID   string `json:"captchaId"`
		CaptchaCode string `json:"captchaCode"`
	}
	if c.ShouldBindJSON(&in) != nil {
		c.JSON(400, gin.H{"error": "invalid registration"})
		return
	}
	u := User{Username: strings.ToLower(strings.TrimSpace(in.Username)), DisplayName: strings.TrimSpace(in.DisplayName), Role: "user", MaxDevices: 5, BandwidthBitsPerSecond: a.r.node.Load().DefaultBandwidthBitsPerSecond, SessionVersion: 1}
	if u.DisplayName == "" {
		u.DisplayName = u.Username
	}
	if !validUser(u) || !validLabel(u.DisplayName, true) || !validPassword(in.Password) {
		c.JSON(400, gin.H{"error": "invalid registration; username 3-48 lowercase ASCII characters, password 12-72 bytes"})
		return
	}
	allowed, err := a.loginAllowed(c.Request.Context(), c.ClientIP(), u.Username)
	if err != nil {
		apiUnavailable(c)
		return
	}
	if !allowed {
		c.Header("Retry-After", "900")
		c.JSON(429, gin.H{"error": "too many registration attempts; retry in 15 minutes"})
		return
	}
	if ok, err := a.consumeCaptcha(c.Request.Context(), in.CaptchaID, in.CaptchaCode); err != nil {
		apiUnavailable(c)
		return
	} else if !ok {
		c.JSON(400, gin.H{"error": "invalid or expired image captcha"})
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
	u.BandwidthBitsPerSecond = a.r.node.Load().DefaultBandwidthBitsPerSecond
	var count int64
	if a.r.db.Model(&User{}).Where("username = ?", u.Username).Count(&count).Error != nil {
		apiUnavailable(c)
		return
	}
	if count > 0 {
		c.JSON(409, gin.H{"error": "username already exists"})
		return
	}
	err = a.r.db.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(&u).Error; err != nil {
			return err
		}
		return audit(tx, u.ID, "user.register", userTarget(u.ID))
	})
	if err != nil {
		apiUnavailable(c)
		return
	}
	c.JSON(201, u)
}

func (a *Console) login(c *gin.Context) {
	var in struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if c.ShouldBindJSON(&in) != nil || len(in.Username) > 48 || len(in.Password) > 72 {
		c.JSON(400, gin.H{"error": "invalid login"})
		return
	}
	in.Username = strings.ToLower(strings.TrimSpace(in.Username))
	allowed, err := a.loginAllowed(c.Request.Context(), c.ClientIP(), in.Username)
	if err != nil {
		c.JSON(503, gin.H{"error": "login protection unavailable"})
		return
	}
	if !allowed {
		c.Header("Retry-After", "900")
		c.JSON(429, gin.H{"error": "too many login attempts; retry in 15 minutes"})
		return
	}
	var u User
	err = a.r.db.Where("username = ?", in.Username).First(&u).Error
	hash := []byte(u.PasswordHash)
	if err != nil {
		hash = a.dummyHash
	}
	passwordErr := bcrypt.CompareHashAndPassword(hash, []byte(in.Password))
	if err != nil || passwordErr != nil || u.Disabled {
		c.JSON(401, gin.H{"error": "invalid username or password"})
		return
	}
	raw, err := randomSecret()
	if err != nil {
		apiUnavailable(c)
		return
	}
	csrf, err := randomSecret()
	if err != nil {
		apiUnavailable(c)
		return
	}
	s := ConsoleSession{ID: hashCredential(raw), UserID: u.ID, Version: u.SessionVersion, CSRF: csrf, ExpiresAt: time.Now().UTC().Add(consoleSessionTTL)}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	// Do not issue a usable session if the user changed while bcrypt ran.
	var latest User
	if a.r.db.First(&latest, u.ID).Error != nil || latest.Disabled || latest.SessionVersion != u.SessionVersion {
		c.JSON(401, gin.H{"error": "account changed; sign in again"})
		return
	}
	if err = a.saveSession(c.Request.Context(), s); err != nil {
		apiUnavailable(c)
		return
	}
	if audit(a.r.db, u.ID, "auth.login", userTarget(u.ID)) != nil {
		_ = a.deleteSession(c.Request.Context(), s)
		apiUnavailable(c)
		return
	}
	a.cookie(c, raw, int(consoleSessionTTL.Seconds()))
	c.JSON(200, gin.H{"user": latest, "csrfToken": csrf})
}
func (a *Console) logout(c *gin.Context) {
	if a.deleteSession(c.Request.Context(), c.MustGet("session").(ConsoleSession)) != nil {
		apiUnavailable(c)
		return
	}
	a.cookie(c, "", -1)
	c.JSON(200, gin.H{"ok": true})
}
func (a *Console) password(c *gin.Context) {
	var in struct {
		CurrentPassword string `json:"currentPassword"`
		NewPassword     string `json:"newPassword"`
	}
	if c.ShouldBindJSON(&in) != nil || !validPassword(in.NewPassword) || len(in.CurrentPassword) > 72 {
		c.JSON(400, gin.H{"error": "password must contain 12-72 bytes"})
		return
	}
	u := currentUser(c)
	if bcrypt.CompareHashAndPassword([]byte(u.PasswordHash), []byte(in.CurrentPassword)) != nil {
		c.JSON(403, gin.H{"error": "current password is incorrect"})
		return
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(in.NewPassword), bcrypt.DefaultCost)
	if err != nil {
		apiUnavailable(c)
		return
	}
	a.r.policyMu.Lock()
	defer a.r.policyMu.Unlock()
	err = a.r.db.Transaction(func(tx *gorm.DB) error {
		result := tx.Model(&User{}).Where("id = ? AND session_version = ?", u.ID, u.SessionVersion).Updates(map[string]any{"password_hash": string(hash), "session_version": gorm.Expr("session_version + 1")})
		if result.Error != nil {
			return result.Error
		}
		if result.RowsAffected != 1 {
			return errPolicy
		}
		return audit(tx, u.ID, "auth.password", userTarget(u.ID))
	})
	if err != nil {
		apiUnavailable(c)
		return
	}
	a.cookie(c, "", -1)
	c.JSON(200, gin.H{"ok": true})
}
