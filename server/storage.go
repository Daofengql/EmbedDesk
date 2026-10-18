// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"context"
	"errors"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/gin-gonic/gin"
	"github.com/glebarez/sqlite"
	"github.com/go-sql-driver/mysql"
	"github.com/redis/go-redis/v9"
	mysqldriver "gorm.io/driver/mysql"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"
)

func (c RelayConfig) driver() string {
	if c.DatabaseDriver == "" {
		return "sqlite"
	}
	return c.DatabaseDriver
}
func (c RelayConfig) validateConsole() error {
	if c.driver() != "sqlite" && c.driver() != "mysql" {
		return errors.New("databaseDriver must be sqlite or mysql")
	}
	if c.driver() == "mysql" {
		d, err := mysql.ParseDSN(c.MySQLDSN)
		if err != nil || d.DBName == "" {
			return errors.New("mysqlDsn must specify a database")
		}
	}
	if c.RegistrationEnabled && !c.ConsoleEnabled {
		return errors.New("registrationEnabled requires consoleEnabled")
	}
	if c.RedisURL != "" {
		if !c.ConsoleEnabled {
			return errors.New("redisUrl requires consoleEnabled")
		}
		if _, err := redis.ParseURL(c.RedisURL); err != nil {
			return errors.New("invalid redisUrl")
		}
	}
	if len(c.RedisPrefix) > 100 || strings.ContainsAny(c.RedisPrefix, " \r\n\t") {
		return errors.New("invalid redisPrefix")
	}
	if c.PublicURL != "" {
		u, err := url.Parse(c.PublicURL)
		if err != nil || u.Host == "" || (u.Scheme != "http" && u.Scheme != "https") || u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") {
			return errors.New("publicUrl must be an HTTP(S) origin without credentials or a path")
		}
		if _, err := relayConnectionCode(c.RelayName, c.PublicURL, ""); err != nil {
			return err
		}
		if c.ConsoleEnabled && u.Scheme == "https" && !c.CookieSecure {
			return errors.New("HTTPS console requires cookieSecure")
		}
	}
	if (c.BootstrapUsername == "") != (c.BootstrapPassword == "") {
		return errors.New("bootstrap username and password must both be supplied")
	}
	if c.BootstrapUsername != "" && !c.ConsoleEnabled {
		return errors.New("bootstrap requires consoleEnabled")
	}
	return nil
}

func (c *RelayConfig) loadConsoleEnv() error {
	for key, dest := range map[string]*string{
		"DATABASE_DRIVER": &c.DatabaseDriver, "MYSQL_DSN": &c.MySQLDSN, "REDIS_URL": &c.RedisURL,
		"REDIS_PREFIX": &c.RedisPrefix, "PUBLIC_URL": &c.PublicURL,
		"RELAY_NAME":         &c.RelayName,
		"BOOTSTRAP_USERNAME": &c.BootstrapUsername, "BOOTSTRAP_PASSWORD": &c.BootstrapPassword,
	} {
		if value, ok := os.LookupEnv("EMBEDDESK_" + key); ok {
			*dest = value
		}
	}
	for key, dest := range map[string]*bool{"CONSOLE_ENABLED": &c.ConsoleEnabled, "COOKIE_SECURE": &c.CookieSecure, "REGISTRATION_ENABLED": &c.RegistrationEnabled} {
		if value, ok := os.LookupEnv("EMBEDDESK_" + key); ok {
			parsed, err := strconv.ParseBool(value)
			if err != nil {
				return errors.New("invalid boolean EMBEDDESK_" + key)
			}
			*dest = parsed
		}
	}
	return nil
}

func openRelayDatabase(c RelayConfig) (*gorm.DB, error) {
	var dialect gorm.Dialector
	if c.driver() == "mysql" {
		d, err := mysql.ParseDSN(c.MySQLDSN)
		if err != nil {
			return nil, errors.New("invalid mysql DSN")
		}
		d.ParseTime, d.Loc = true, time.UTC
		d.Timeout, d.ReadTimeout, d.WriteTimeout = 5*time.Second, 10*time.Second, 10*time.Second
		d.MultiStatements = false
		dialect = mysqldriver.Open(d.FormatDSN())
	} else {
		if c.Database != ":memory:" {
			if err := os.MkdirAll(filepath.Dir(c.Database), 0700); err != nil {
				return nil, err
			}
		}
		dialect = sqlite.Open(c.Database)
	}
	db, err := gorm.Open(dialect, &gorm.Config{Logger: logger.Default.LogMode(logger.Silent)})
	if err != nil {
		return nil, errors.New("database connection failed")
	}
	sqlDB, err := db.DB()
	if err != nil {
		return nil, err
	}
	if c.driver() == "sqlite" {
		sqlDB.SetMaxOpenConns(1)
		for _, query := range []string{"PRAGMA busy_timeout=5000", "PRAGMA journal_mode=WAL"} {
			if err := db.Exec(query).Error; err != nil {
				_ = sqlDB.Close()
				return nil, err
			}
		}
	} else {
		sqlDB.SetMaxOpenConns(16)
		sqlDB.SetMaxIdleConns(4)
		sqlDB.SetConnMaxLifetime(5 * time.Minute)
	}
	return db, nil
}

func (r *Relay) readiness(c *gin.Context) {
	ctx, cancel := context.WithTimeout(c.Request.Context(), 2*time.Second)
	defer cancel()
	db, err := r.db.DB()
	if err != nil || db.PingContext(ctx) != nil {
		c.JSON(503, gin.H{"status": "unavailable"})
		return
	}
	if r.console != nil && r.console.redis != nil && r.console.redis.Ping(ctx).Err() != nil {
		c.JSON(503, gin.H{"status": "unavailable"})
		return
	}
	c.JSON(200, gin.H{"status": "ok", "protocol": 1})
}
