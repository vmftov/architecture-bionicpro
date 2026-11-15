package main

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"time"

	"github.com/bsm/redislock"
	"github.com/redis/go-redis/v9"
	"golang.org/x/oauth2"
)

type RedisSessionToken struct {
	Token              *oauth2.Token `json:"token"`
	RefreshTokenExpiry time.Time     `json:"refresh_token_expiry,omitempty"`
}

type RedisSessionsManager struct {
	redisClient       *redis.Client     // клиент
	redisLocker       *redislock.Client // локер для распред блокировки
	encryptionKey     []byte            // ключ для шифрования токена
	defaultSessionTTL time.Duration     // время жизни сессии по умолчанию
}

func NewRedisSessionsManager(redisAddr string, encryptionKey []byte, defaultSessionTTL time.Duration) *RedisSessionsManager {
	var cl = redis.NewClient(&redis.Options{Addr: redisAddr})
	var lc = redislock.New(cl)

	return &RedisSessionsManager{
		redisClient:       cl,
		redisLocker:       lc,
		encryptionKey:     encryptionKey,
		defaultSessionTTL: defaultSessionTTL,
	}
}

func (m *RedisSessionsManager) NewRedisSessionToken(token *oauth2.Token) *RedisSessionToken {
	var refreshTokenExpiryIn time.Duration
	if expiryIn, ok := token.Extra("refresh_expires_in").(float64); ok {
		refreshTokenExpiryIn = time.Duration(expiryIn) * time.Second
	} else {
		refreshTokenExpiryIn = m.defaultSessionTTL
	}

	return &RedisSessionToken{
		Token:              token,
		RefreshTokenExpiry: time.Now().Add(refreshTokenExpiryIn),
	}
}

func (m *RedisSessionsManager) Close() {
	if m.redisClient != nil {
		m.redisClient.Close()
	}
}

func (m *RedisSessionsManager) Lock(ctx context.Context, sessionId string) (*redislock.Lock, error) {
	lockKey := "lock:" + getSessionKey(sessionId)
	backoff := redislock.LinearBackoff(50 * time.Millisecond)
	return m.redisLocker.Obtain(ctx, lockKey, 5*time.Second, &redislock.Options{
		RetryStrategy: backoff,
	})
}

func (m *RedisSessionsManager) readToken(ctx context.Context, sessionId string) (*RedisSessionToken, error) {
	ciphertext, err := m.redisClient.Get(ctx, getSessionKey(sessionId)).Bytes()
	if err != nil {
		return nil, err
	}
	plaintext, err := decryptAESGCM(ciphertext, m.encryptionKey)
	if err != nil {
		return nil, err
	}
	var token RedisSessionToken
	if err := json.Unmarshal(plaintext, &token); err != nil {
		return nil, err
	}
	return &token, nil
}

func (m *RedisSessionsManager) saveToken(ctx context.Context, sessionId string, token *RedisSessionToken, prevSessionId string) error {
	plaintext, err := json.Marshal(token)
	if err != nil {
		return err
	}
	ciphertext, err := encryptAESGCM(plaintext, m.encryptionKey)
	if err != nil {
		return err
	}
	ttl := time.Until(token.RefreshTokenExpiry)
	if ttl <= 0 {
		return fmt.Errorf("TTL сессии истёк")
	}
	if prevSessionId != "" {
		err = m.redisClient.Del(ctx, getSessionKey(prevSessionId)).Err()
		if err != nil {
			return err
		}
	}
	return m.redisClient.Set(ctx, getSessionKey(sessionId), ciphertext, ttl).Err()
}

func getSessionKey(sessionId string) string {
	return "session:" + sessionId
}

func encryptAESGCM(plaintext, key []byte) ([]byte, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}
	return gcm.Seal(nonce, nonce, plaintext, nil), nil
}

func decryptAESGCM(ciphertext, key []byte) ([]byte, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	nonceSize := gcm.NonceSize()
	if len(ciphertext) < nonceSize {
		return nil, errors.New("ciphertext too short")
	}
	nonce, encryptedData := ciphertext[:nonceSize], ciphertext[nonceSize:]
	return gcm.Open(nil, nonce, encryptedData, nil)
}
