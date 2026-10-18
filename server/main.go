// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"embed"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/gin-gonic/gin"
	"github.com/gorilla/websocket"
	"golang.org/x/time/rate"
	"gorm.io/gorm"
)

const maxMessage = 96 * 1024
const defaultBandwidthBitsPerSecond int64 = 1_000_000
const minBandwidthBitsPerSecond int64 = 8_000
const maxBandwidthBitsPerSecond int64 = 100_000_000_000

type RelayConfig struct {
	Listen                 string `json:"listen"`
	Database               string `json:"database"`
	AdmissionKey           string `json:"admissionKey"`
	BandwidthBitsPerSecond int64  `json:"bandwidthBitsPerSecond"`
	DatabaseDriver         string `json:"databaseDriver"`
	MySQLDSN               string `json:"mysqlDsn"`
	RedisURL               string `json:"redisUrl"`
	RedisPrefix            string `json:"redisPrefix"`
	ConsoleEnabled         bool   `json:"consoleEnabled"`
	RegistrationEnabled    bool   `json:"registrationEnabled"`
	CookieSecure           bool   `json:"cookieSecure"`
	PublicURL              string `json:"publicUrl"`
	RelayName              string `json:"relayName"`
	BootstrapUsername      string `json:"-"`
	BootstrapPassword      string `json:"-"`
}

func defaultRelayConfig() RelayConfig {
	return RelayConfig{Listen: "127.0.0.1:7080", Database: "data/relay.sqlite", BandwidthBitsPerSecond: defaultBandwidthBitsPerSecond,
		DatabaseDriver: "sqlite", CookieSecure: true, RedisPrefix: "embeddesk:", RelayName: "EmbedDesk 中继"}
}

func (c RelayConfig) validate() error {
	if strings.TrimSpace(c.Listen) == "" || (c.driver() == "sqlite" && strings.TrimSpace(c.Database) == "") {
		return errors.New("listen and database are required")
	}
	_, port, err := net.SplitHostPort(c.Listen)
	if err != nil {
		return errors.New("listen must be HOST:PORT")
	}
	number, err := strconv.Atoi(port)
	if err != nil || number < 1 || number > 65535 {
		return errors.New("listen port must be between 1 and 65535")
	}
	if c.BandwidthBitsPerSecond < minBandwidthBitsPerSecond || c.BandwidthBitsPerSecond > maxBandwidthBitsPerSecond {
		return fmt.Errorf("bandwidthBitsPerSecond must be between %d and %d", minBandwidthBitsPerSecond, maxBandwidthBitsPerSecond)
	}
	return c.validateConsole()
}

//go:embed licenses/*
var licenses embed.FS

type Device struct {
	ID             string `gorm:"primaryKey;size:24"`
	CredentialHash string `gorm:"uniqueIndex;size:64;not null"`
	CreatedAt      time.Time
	OwnerID        *uint  `gorm:"index"`
	Label          string `gorm:"size:80"`
	Disabled       bool
	LastSeenAt     *time.Time
}

// Body is an opaque, bounded protocol message. Passwords and traffic never enter SQLite.
type Message struct {
	Type    string          `json:"type"`
	Session string          `json:"session,omitempty"`
	From    string          `json:"from,omitempty"`
	To      string          `json:"to,omitempty"`
	Body    json.RawMessage `json:"body,omitempty"`
	Error   string          `json:"error,omitempty"`
}

type peer struct {
	id        string
	ownerID   uint
	socket    *websocket.Conn
	out       chan Message
	dataOut   chan Message
	dataIn    chan Message
	done      chan struct{}
	once      sync.Once
	budget    *rate.Limiter
	control   *rate.Limiter
	written   chan struct{}
	processed chan struct{}
}

func (p *peer) stop() { p.once.Do(func() { close(p.done); _ = p.socket.Close() }) }
func (p *peer) send(m Message) bool {
	select {
	case <-p.done:
		return false
	default:
	}
	queue := p.out
	if m.Type == "data" {
		queue = p.dataOut
	}
	select {
	case queue <- m:
		return true
	default:
		p.stop()
		return false
	}
}

type session struct {
	origin, owner string
	accepted      bool
	stage         int
	expires       time.Time
	createdAt     time.Time
}
type Relay struct {
	db              *gorm.DB
	mu              sync.Mutex
	peers           map[string]*peer
	sessions        map[string]*session
	closed          bool
	admissionBudget *rate.Limiter
	stop            chan struct{}
	once            sync.Once
	workers         sync.WaitGroup
	policyMu        sync.Mutex // Serialize registration, admission and policy changes.
	config          RelayConfig
	console         *Console
	startedAt       time.Time
	node            atomic.Pointer[NodeSettings]
	metrics         atomic.Pointer[ServerMetrics]
	receivedBytes   atomic.Uint64
	sentBytes       atomic.Uint64
}

func newRelay(path, admission string) (*Relay, error) {
	return newRelayWithConfig(RelayConfig{Listen: "127.0.0.1:7080", Database: path, AdmissionKey: admission, BandwidthBitsPerSecond: defaultBandwidthBitsPerSecond})
}

func newRelayWithConfig(config RelayConfig) (*Relay, error) {
	if err := config.validate(); err != nil {
		return nil, err
	}
	db, err := openRelayDatabase(config)
	if err != nil {
		return nil, err
	}
	sqlDB, err := db.DB()
	if err != nil {
		return nil, err
	}
	if err = db.AutoMigrate(&Device{}); err != nil {
		_ = sqlDB.Close()
		return nil, err
	}
	r := &Relay{db: db, peers: make(map[string]*peer), sessions: make(map[string]*session), admissionBudget: rate.NewLimiter(20, 40), stop: make(chan struct{}), config: config, startedAt: time.Now()}
	settings := initialNodeSettings(config)
	r.node.Store(&settings)
	if config.ConsoleEnabled {
		r.console, err = newConsole(r)
		if err != nil {
			_ = sqlDB.Close()
			return nil, err
		}
		r.config.BootstrapPassword = ""
	}
	telemetry := &telemetrySampler{}
	if r.console != nil {
		r.sampleTelemetry(telemetry)
	}
	r.workers.Add(1)
	go func() {
		defer r.workers.Done()
		ticker := time.NewTicker(time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-r.stop:
				return
			case <-ticker.C:
				if r.console != nil {
					r.sampleTelemetry(telemetry)
				}
				r.mu.Lock()
				for id, s := range r.sessions {
					if !s.accepted && time.Now().After(s.expires) {
						for _, device := range []string{s.owner, s.origin} {
							if p := r.peers[device]; p != nil {
								p.send(Message{Type: "close", Session: id, Error: "authorization expired"})
							}
						}
						delete(r.sessions, id)
					}
				}
				r.mu.Unlock()
			}
		}
	}()
	return r, nil
}

func (r *Relay) router() *gin.Engine {
	gin.SetMode(gin.ReleaseMode)
	g := gin.New() // No request/body/token logging.
	_ = g.SetTrustedProxies(nil)
	g.Use(gin.Recovery(), func(c *gin.Context) {
		c.Request.Body = http.MaxBytesReader(c.Writer, c.Request.Body, maxMessage)
		if strings.HasPrefix(c.Request.URL.Path, "/v1/") && !r.admissionBudget.Allow() {
			c.AbortWithStatusJSON(429, gin.H{"error": "admission rate limit"})
			return
		}
		c.Next()
	})
	g.GET("/health", func(c *gin.Context) { c.JSON(200, gin.H{"status": "ok", "protocol": 1}) })
	g.POST("/v1/register", r.register)
	g.GET("/v1/devices/:id", r.lookup)
	g.GET("/v1/channel", r.channel)
	g.GET("/ready", r.readiness)
	if r.console != nil {
		r.console.routes(g)
	}
	return g
}
func hashCredential(value string) string {
	digest := sha256.Sum256([]byte(value))
	return hex.EncodeToString(digest[:])
}
func bearer(c *gin.Context) string {
	return strings.TrimPrefix(c.GetHeader("Authorization"), "Bearer ")
}
func (r *Relay) authenticate(c *gin.Context) (string, bool) {
	value := bearer(c)
	if len(value) != 44 {
		c.JSON(401, gin.H{"error": "device authentication failed"})
		return "", false
	}
	var device Device
	if err := r.db.Where("id = ? AND credential_hash = ?", c.GetHeader("X-Device-ID"), hashCredential(value)).First(&device).Error; err != nil {
		c.JSON(401, gin.H{"error": "device authentication failed"})
		return "", false
	}
	if _, err := r.devicePolicy(device); err != nil {
		c.JSON(403, gin.H{"error": "device or owner is disabled"})
		return "", false
	}
	c.Set("device", device)
	return device.ID, true
}
func (r *Relay) register(c *gin.Context) {
	r.policyMu.Lock()
	defer r.policyMu.Unlock()
	if !r.admissionAllowed(bearer(c)) {
		c.JSON(401, gin.H{"error": "admission denied"})
		return
	}
	var input struct {
		Credential string `json:"credential"`
	}
	if c.ShouldBindJSON(&input) != nil {
		c.JSON(400, gin.H{"error": "invalid registration"})
		return
	}
	raw, err := base64.StdEncoding.DecodeString(input.Credential)
	if err != nil || len(raw) != 32 {
		c.JSON(400, gin.H{"error": "credential must contain 32 random bytes"})
		return
	}
	digest := hashCredential(input.Credential)
	// Stable pseudonymous ID; never depends on machine serial numbers or account names.
	device, bits, err := r.registerDevice(digest)
	if err != nil {
		c.JSON(403, gin.H{"error": "device registration denied or storage unavailable"})
		return
	}
	c.JSON(200, gin.H{"id": device.ID, "bitsPerSecond": bits, "protocol": 1})
}
func (r *Relay) lookup(c *gin.Context) {
	if _, ok := r.authenticate(c); !ok {
		return
	}
	r.mu.Lock()
	_, online := r.peers[c.Param("id")]
	r.mu.Unlock()
	c.JSON(200, gin.H{"id": c.Param("id"), "online": online})
}

var upgrade = websocket.Upgrader{ReadBufferSize: 4096, WriteBufferSize: 4096, HandshakeTimeout: 10 * time.Second,
	CheckOrigin: func(req *http.Request) bool { return req.Header.Get("Origin") == "" }}

func (r *Relay) channel(c *gin.Context) {
	r.policyMu.Lock()
	id, ok := r.authenticate(c)
	if !ok {
		r.policyMu.Unlock()
		return
	}
	device := c.MustGet("device").(Device)
	bits, policyErr := r.devicePolicy(device)
	if policyErr != nil {
		r.policyMu.Unlock()
		c.JSON(403, gin.H{"error": "device or owner is disabled"})
		return
	}
	socket, err := upgrade.Upgrade(c.Writer, c.Request, nil)
	if err != nil {
		r.policyMu.Unlock()
		return
	}
	bytesPerSecond := float64(bits) / 8
	p := &peer{id: id, socket: socket, out: make(chan Message, 64), dataOut: make(chan Message, 64), dataIn: make(chan Message, 64), done: make(chan struct{}), written: make(chan struct{}), processed: make(chan struct{}), budget: rate.NewLimiter(rate.Limit(bytesPerSecond), 64*1024), control: rate.NewLimiter(20, 40)}
	if device.OwnerID != nil {
		p.ownerID = *device.OwnerID
	}
	r.mu.Lock()
	if r.closed || len(r.peers) >= 1024 || r.peers[id] != nil {
		r.mu.Unlock()
		r.policyMu.Unlock()
		p.stop()
		return
	}
	// Account devices share one bidirectional budget. Anonymous peers each keep their own.
	// Reuse only active peers: a policy update stops old peers before new admission.
	if p.ownerID != 0 {
		for _, existing := range r.peers {
			if existing.ownerID != p.ownerID {
				continue
			}
			select {
			case <-existing.done:
				continue
			default:
			}
			p.budget = existing.budget
			break
		}
	}
	r.peers[id] = p
	r.workers.Add(1)
	r.mu.Unlock()
	r.policyMu.Unlock()
	defer r.remove(p)
	defer r.workers.Done()
	socket.SetReadLimit(maxMessage)
	_ = socket.SetReadDeadline(time.Now().Add(75 * time.Second))
	socket.SetPongHandler(func(string) error { return socket.SetReadDeadline(time.Now().Add(75 * time.Second)) })
	go r.writeLoop(p)
	go r.processData(p)
	defer func() { p.stop(); <-p.written; <-p.processed }()
	p.send(Message{Type: "online", From: id})
	for {
		var m Message
		if err = socket.ReadJSON(&m); err != nil {
			return
		}
		_ = socket.SetReadDeadline(time.Now().Add(75 * time.Second))
		if m.Type != "data" && !p.control.Allow() {
			return
		}
		if m.Type == "heartbeat" {
			p.send(Message{Type: "heartbeat"})
			continue
		}
		if m.Type == "data" {
			if len(m.Body) > 64*1024 {
				return
			}
			// Keep reading Ping/Pong and heartbeats while data waits for bandwidth.
			select {
			case p.dataIn <- m:
			default:
				return
			}
			continue
		}
		r.route(p, m)
	}
}
func (r *Relay) processData(p *peer) {
	defer close(p.processed)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() {
		select {
		case <-p.done:
			cancel()
		case <-ctx.Done():
		}
	}()
	for {
		select {
		case <-p.done:
			return
		case m := <-p.dataIn:
			if p.budget.WaitN(ctx, len(m.Body)) != nil {
				return
			}
			r.route(p, m)
		}
	}
}
func (r *Relay) writeLoop(p *peer) {
	defer close(p.written)
	ticker := time.NewTicker(25 * time.Second)
	defer ticker.Stop()
	defer p.stop()
	write := func(m Message) bool {
		_ = p.socket.SetWriteDeadline(time.Now().Add(15 * time.Second))
		if p.socket.WriteJSON(m) != nil {
			return false
		}
		if m.Type == "data" {
			r.sentBytes.Add(uint64(len(m.Body)))
		}
		return true
	}
	ping := func() bool {
		return p.socket.WriteControl(websocket.PingMessage, nil, time.Now().Add(10*time.Second)) == nil
	}
	for {
		// Control is independent of data reservations and cannot starve behind a slow quota.
		select {
		case m := <-p.out:
			if !write(m) {
				return
			}
			continue
		default:
		}
		select {
		case <-p.done:
			return
		case <-ticker.C:
			if !ping() {
				return
			}
		case m := <-p.out:
			if !write(m) {
				return
			}
		case m := <-p.dataOut:
			reservation := p.budget.ReserveN(time.Now(), len(m.Body))
			if !reservation.OK() {
				return
			}
			timer := time.NewTimer(reservation.Delay())
			ready := false
			for !ready {
				select {
				case <-p.done:
					timer.Stop()
					reservation.Cancel()
					return
				case <-timer.C:
					ready = true
				case <-ticker.C:
					if !ping() {
						timer.Stop()
						reservation.Cancel()
						return
					}
				case control := <-p.out:
					if !write(control) {
						timer.Stop()
						reservation.Cancel()
						return
					}
				}
			}
			if !write(m) {
				return
			}
		}
	}
}
func (r *Relay) route(p *peer, m Message) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for id, s := range r.sessions {
		if !s.accepted && time.Now().After(s.expires) {
			delete(r.sessions, id)
		}
	}
	fail := func(reason string) {
		response := Message{Type: "error", Session: m.Session, Error: reason}
		if m.Type == "connect" {
			var hello struct {
				RequestID string `json:"requestId"`
			}
			if json.Unmarshal(m.Body, &hello) == nil && len(hello.RequestID) == 32 {
				response.Body, _ = json.Marshal(hello)
			}
		}
		p.send(response)
	}
	if m.Type == "connect" {
		target := r.peers[m.To]
		if target == nil || m.To == p.id {
			fail("target offline or invalid")
			return
		}
		if len(r.sessions) >= 4096 {
			fail("session capacity reached")
			return
		}
		count, targetCount := 0, 0
		for _, s := range r.sessions {
			if s.origin == p.id || s.owner == p.id {
				count++
			}
			if s.origin == m.To || s.owner == m.To {
				targetCount++
			}
		}
		if count >= 16 || targetCount >= 16 {
			fail("device session capacity reached")
			return
		}
		sid := make([]byte, 16)
		if _, err := rand.Read(sid); err != nil {
			fail("random source unavailable")
			return
		}
		m.Session = hex.EncodeToString(sid)
		r.sessions[m.Session] = &session{origin: p.id, owner: m.To, expires: time.Now().Add(90 * time.Second), createdAt: time.Now().UTC()}
		m.From = p.id
		if !target.send(m) {
			delete(r.sessions, m.Session)
			fail("target unavailable")
		}
		p.send(Message{Type: "pending", Session: m.Session, Body: m.Body})
		return
	}
	s := r.sessions[m.Session]
	if s == nil && (m.Type == "close" || m.Type == "data") {
		return
	} // Close is idempotent; queued data for an ended session must not create a spurious error.
	if s == nil || (s.origin != p.id && s.owner != p.id) {
		fail("session unavailable")
		return
	}
	targetID := s.owner
	if p.id == s.owner {
		targetID = s.origin
	}
	target := r.peers[targetID]
	if target == nil {
		delete(r.sessions, m.Session)
		fail("peer disconnected")
		return
	}
	switch m.Type {
	case "challenge", "accept":
		if p.id != s.owner {
			fail("invalid session role")
			return
		}
		if m.Type == "challenge" {
			if s.stage != 0 {
				fail("invalid handshake order")
				return
			}
			s.stage = 1
		}
		if m.Type == "accept" {
			if s.stage != 2 {
				fail("invalid handshake order")
				return
			}
			s.accepted = true
			s.stage = 3
		}
	case "proof":
		if p.id != s.origin {
			fail("invalid session role")
			return
		}
		if s.stage != 1 {
			fail("invalid handshake order")
			return
		}
		s.stage = 2
	case "data", "relay":
		if !s.accepted {
			fail("session not authorized")
			return
		}
	case "close":
		delete(r.sessions, m.Session)
	default:
		fail("unsupported message")
		return
	}
	if !s.accepted && time.Now().After(s.expires) {
		delete(r.sessions, m.Session)
		fail("authorization expired")
		return
	}
	m.From = p.id
	m.To = targetID
	if target.send(m) && m.Type == "data" {
		r.receivedBytes.Add(uint64(len(m.Body)))
	}
}
func (r *Relay) remove(p *peer) {
	p.stop()
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.peers[p.id] != p {
		return
	}
	delete(r.peers, p.id)
	for id, s := range r.sessions {
		if s.origin == p.id || s.owner == p.id {
			other := s.owner
			if other == p.id {
				other = s.origin
			}
			if q := r.peers[other]; q != nil {
				q.send(Message{Type: "close", Session: id, Error: "peer disconnected"})
			}
			delete(r.sessions, id)
		}
	}
}
func (r *Relay) close() error {
	var err error
	r.once.Do(func() {
		r.mu.Lock()
		r.closed = true
		close(r.stop)
		for _, p := range r.peers {
			p.stop()
		}
		r.mu.Unlock()
		r.workers.Wait()
		if r.console != nil {
			r.console.close()
		}
		db, e := r.db.DB()
		if e != nil {
			err = e
		} else {
			err = db.Close()
		}
	})
	return err
}
func main() {
	if len(os.Args) == 2 && os.Args[1] == "--licenses" {
		_ = fs.WalkDir(licenses, "licenses", func(path string, entry fs.DirEntry, err error) error {
			if err != nil {
				return err
			}
			if !entry.IsDir() {
				data, readErr := licenses.ReadFile(path)
				if readErr != nil {
					return readErr
				}
				fmt.Printf("\n=== %s ===\n%s\n", path, data)
			}
			return nil
		})
		_ = fs.WalkDir(consoleAssets, "www/dist/licenses", func(path string, entry fs.DirEntry, err error) error {
			if err != nil {
				return err
			}
			if !entry.IsDir() {
				data, readErr := consoleAssets.ReadFile(path)
				if readErr != nil {
					return readErr
				}
				fmt.Printf("\n=== %s ===\n%s\n", path, data)
			}
			return nil
		})
		return
	}
	config, err := loadRelayConfig()
	if err != nil {
		log.Fatal("configuration failed: ", err)
	}
	if len(os.Args) > 1 {
		if len(os.Args) != 4 || os.Args[1] != "--connection-code" {
			log.Fatal("usage: relay [--licenses | --connection-code PUBLIC_URL NAME]")
		}
		code, codeErr := relayConnectionCode(os.Args[3], os.Args[2], config.AdmissionKey)
		if codeErr != nil {
			log.Fatal("connection code export failed: ", codeErr)
		}
		fmt.Println(code)
		return
	}
	relay, err := newRelayWithConfig(config)
	if err != nil {
		log.Fatal("database initialization failed")
	}
	defer relay.close()
	server := &http.Server{Addr: config.Listen, Handler: relay.router(), ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 90 * time.Second, MaxHeaderBytes: 16 * 1024}
	stopped := make(chan os.Signal, 1)
	signal.Notify(stopped, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-stopped
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		_ = server.Shutdown(ctx)
		_ = relay.close()
	}()
	fmt.Printf("EmbedDesk relay protocol 1; bandwidth %d bits/s; HTTP only; console enabled: %t\n", config.BandwidthBitsPerSecond, config.ConsoleEnabled)
	err = server.ListenAndServe()
	if err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal("HTTP listener failed")
	}
}

func loadRelayConfig() (RelayConfig, error) {
	config := defaultRelayConfig()
	if path := os.Getenv("EMBEDDESK_CONFIG"); path != "" {
		file, err := os.Open(path)
		if err != nil {
			return RelayConfig{}, err
		}
		defer file.Close()
		data, err := io.ReadAll(io.LimitReader(file, 64*1024+1))
		if err != nil {
			return RelayConfig{}, err
		}
		if len(data) > 64*1024 {
			return RelayConfig{}, errors.New("configuration exceeds 64 KiB")
		}
		if trimmed := bytes.TrimSpace(data); len(trimmed) == 0 || trimmed[0] != '{' {
			return RelayConfig{}, errors.New("configuration must be a JSON object")
		}
		decoder := json.NewDecoder(bytes.NewReader(data))
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&config); err != nil {
			return RelayConfig{}, err
		}
		if decoder.Decode(new(any)) != io.EOF {
			return RelayConfig{}, errors.New("configuration must contain one JSON object")
		}
	}
	// Environment overrides remain useful for service managers and local smoke tests.
	if value := os.Getenv("EMBEDDESK_LISTEN"); value != "" {
		config.Listen = value
	}
	if value := os.Getenv("EMBEDDESK_DATABASE"); value != "" {
		config.Database = value
	}
	if value := os.Getenv("EMBEDDESK_ADMISSION_KEY"); value != "" {
		config.AdmissionKey = value
	}
	if value := os.Getenv("EMBEDDESK_BANDWIDTH_BITS_PER_SECOND"); value != "" {
		parsed, err := strconv.ParseInt(value, 10, 64)
		if err != nil {
			return RelayConfig{}, fmt.Errorf("invalid EMBEDDESK_BANDWIDTH_BITS_PER_SECOND: %w", err)
		}
		config.BandwidthBitsPerSecond = parsed
	}
	if err := config.loadConsoleEnv(); err != nil {
		return RelayConfig{}, err
	}
	return config, config.validate()
}
