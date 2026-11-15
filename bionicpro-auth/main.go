package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"github.com/google/uuid"
	"github.com/zitadel/oidc/v3/pkg/client/rp"
	httphelper "github.com/zitadel/oidc/v3/pkg/http"
	"github.com/zitadel/oidc/v3/pkg/oidc"
	"golang.org/x/oauth2"
)

type hostRewriteTransport struct {
	base        http.RoundTripper
	publicUrl   *url.URL
	internalUrl *url.URL
}

func (t *hostRewriteTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	if req.URL.Host == t.publicUrl.Host {
		cloned := req.Clone(req.Context())
		cloned.Host = t.publicUrl.Host
		cloned.URL.Scheme = t.internalUrl.Scheme
		cloned.URL.Host = t.internalUrl.Host
		return t.base.RoundTrip(cloned)
	}
	return t.base.RoundTrip(req)
}

var redisSessions *RedisSessionsManager // управляет сессиями в Redis
var oidcProvider rp.RelyingParty        // OIDC провайдер
var oidcHttpClient *http.Client         // HTTP-клиент для внутренних запросов к Keycloak
var resourceUrl *url.URL                // URI ресурсного сервиса
var frontendUrl string                  // URI фронтенда

func main() {
	defer func() {
		if redisSessions != nil {
			redisSessions.Close()
		}
	}()

	encryptionKey, err := hex.DecodeString(readEnvVarOrPanic("ENCRYPTION_KEY"))
	if err != nil {
		log.Fatalf("Не удалось декодировать ключ шифрования сессии. %v", err)
	}
	defaultSessionTtl, err := strconv.Atoi(readEnvVarOrPanic("DEFAULT_SESSION_TTL_MINS"))
	if err != nil || defaultSessionTtl <= 0 {
		log.Fatalf("Задано некорректное значение длительности сессии по умолчанию. %v", err)
	}

	redisSessions = NewRedisSessionsManager(
		readEnvVarOrPanic("REDIS_ADDR"),
		[]byte(encryptionKey),
		time.Duration(defaultSessionTtl)*time.Minute,
	)
	oidcProvider = initOIDCProvider(encryptionKey)
	resourceUrl, _ = url.Parse(readEnvVarOrPanic("RESOURCE_URL"))
	frontendUrl = readEnvVarOrPanic("FRONTEND_URL")

	// статус аутентификации
	http.Handle("GET /auth/status", authMiddleware(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]bool{"authenticated": true})
	})))

	// ping
	http.HandleFunc("GET /status", func(w http.ResponseWriter, req *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]bool{"ok": true})
	})

	// остальные вызовы проксируем
	proxy := &httputil.ReverseProxy{Rewrite: func(req *httputil.ProxyRequest) {
		// при обращении к внутреннему сервису добавляем в заголовок access token
		req.SetURL(resourceUrl)
		token, ok := req.In.Context().Value("access_token").(string)
		if ok {
			req.Out.Header.Set("Authorization", "Bearer "+token)
		}
	}}
	http.Handle("/", authMiddleware(proxy))

	// запускаем сервер
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	httpServer := runHttpServer("3001", stop)
	<-ctx.Done()
	stop()
	shutdownHttpServer(httpServer)
}

func readEnvVarOrPanic(name string) string {
	val := os.Getenv(name)
	if val == "" {
		panic(fmt.Errorf("Не задана переменная окружения %v, используется значение %v", name, val))
	}
	return val
}

func initOIDCProvider(encryptionKey []byte) rp.RelyingParty {
	var redirectUrl = readEnvVarOrPanic("REDIRECT_URL")
	var keycloakUrl = readEnvVarOrPanic("KEYCLOAK_URL")
	var keycloakInternalUrl = readEnvVarOrPanic("KEYCLOAK_INTERNAL_URL")
	var keycloakRealm = readEnvVarOrPanic("KEYCLOAK_REALM")
	var keycloakClientId = readEnvVarOrPanic("KEYCLOAK_CLIENT_ID")
	var keycloakClientSecret = readEnvVarOrPanic("KEYCLOAK_CLIENT_SECRET")

	keycloakPublicUrlVal, _ := url.Parse(keycloakUrl)
	keycloakInternalUrlVal, _ := url.Parse(keycloakInternalUrl)

	oidcHttpClient = &http.Client{
		Transport: &hostRewriteTransport{
			base:        http.DefaultTransport,
			publicUrl:   keycloakPublicUrlVal,
			internalUrl: keycloakInternalUrlVal,
		},
	}

	ctx := context.Background()
	issuer := keycloakUrl + "/realms/" + keycloakRealm
	scopes := []string{oidc.ScopeOpenID, oidc.ScopeEmail}

	// в качестве ключа для HMAC подписи и AES шифрования кук со state и PKCE
	// используем один ключ, чтобы не усложнять учебную работу
	cookieHandler := httphelper.NewCookieHandler(encryptionKey, encryptionKey, httphelper.WithUnsecure())

	options := []rp.Option{
		rp.WithHTTPClient(oidcHttpClient),
		rp.WithCookieHandler(cookieHandler),
		rp.WithPKCE(cookieHandler), // <-- Включаем автоматический PKCE!
		rp.WithVerifierOpts(rp.WithIssuedAtMaxAge(5 * time.Minute)),
	}

	// ждем пока запустится Keycloak
	var provider rp.RelyingParty
	var err = errors.New("")
	for i := 0; i < 10 && err != nil; i++ {
		time.Sleep(5 * time.Second)
		provider, err = rp.NewRelyingPartyOIDC(ctx, issuer, keycloakClientId, keycloakClientSecret, redirectUrl, scopes, options...)
	}
	if err != nil {
		log.Fatalf("Ошибка инициализации OIDC провайдера: %v", err)
	}

	stateGenerator := func() string {
		return uuid.New().String()
	}

	http.Handle("/login", rp.AuthURLHandler(stateGenerator, provider))
	http.Handle("/auth/callback", rp.CodeExchangeHandler(rp.UserinfoCallback(userinfoCallback), provider))

	return provider
}

func userinfoCallback(w http.ResponseWriter, r *http.Request, tokens *oidc.Tokens[*oidc.IDTokenClaims], state string, rp rp.RelyingParty, info *oidc.UserInfo) {
	// сохраняем данные сессии в Redis
	sessionId := uuid.NewString()
	redisToken := redisSessions.NewRedisSessionToken(tokens.Token)
	err := redisSessions.saveToken(r.Context(), sessionId, redisToken, "")
	if err != nil {
		log.Print("Не удалось инициализировать сессию. Повторите вход.")
		http.Error(w, "Не удалось инициализировать сессию. Повторите вход.", http.StatusInternalServerError)
		return
	}
	// сохраняем Id сессии в куке
	setSessionCookie(w, sessionId)
	// перенаправляем обратно на фронтенд
	http.Redirect(w, r, frontendUrl, http.StatusFound)
}

func runHttpServer(port string, stop func()) *http.Server {
	httpServer := &http.Server{
		Addr:    ":" + port,
		Handler: corsMiddleware(http.DefaultServeMux),
	}

	go func() {
		err := httpServer.ListenAndServe()
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Printf("Ошибка HTTP сервера: %v", err)
		}
		stop()
	}()

	return httpServer
}

func corsMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", frontendUrl)
		w.Header().Set("Access-Control-Allow-Credentials", "true")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Authorization")
		if req.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, req)
	})
}

func shutdownHttpServer(httpServer *http.Server) {
	if httpServer == nil {
		return
	}

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	err := httpServer.Shutdown(ctx)
	if err != nil {
		log.Printf("Ошибка при остановке HTTP сервера: %v", err)
	}
}

func setSessionCookie(w http.ResponseWriter, sessionId string) {
	http.SetCookie(w, &http.Cookie{
		Name:     "session_id",
		Value:    sessionId,
		HttpOnly: true,
		Secure:   true,
		SameSite: http.SameSiteLaxMode,
		Path:     "/",
	})
}

func authMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		// получение и проверка id сессии в куке
		sessionIdCookie, err := req.Cookie("session_id")
		if err != nil {
			http.Error(w, "Unauthorized", http.StatusUnauthorized)
			return
		}

		// формируем новый id сессии для задания 12 т.к. в задании прямо
		// указано что нужно заменять сессию при каждом запросе к ресурсу!
		newSessionId := uuid.New().String()
		// выполняем проверку и продление access token при необходимости
		// если refresh token истёк, или обновиться не удлалось по другой причине, прерываем запрос
		token, err := getOrRefreshToken(req.Context(), sessionIdCookie.Value, newSessionId)
		if err != nil {
			http.Error(w, "Unauthorized", http.StatusUnauthorized)
			return
		}

		// сохраняем новый id сессии в куке
		setSessionCookie(w, newSessionId)
		// сохраняем access token в контекст запроса для прокси
		ctx := context.WithValue(req.Context(), "access_token", token.AccessToken)

		// выполняем след шаг
		next.ServeHTTP(w, req.WithContext(ctx))
	})
}

func getOrRefreshToken(ctx context.Context, curSessionId string, newSessionId string) (*oauth2.Token, error) {
	// т.к. по заданию нужно менять сессию при каждом запросе,
	// блокировка нужна чтобы не сломать состояние,
	// хотя, фактически, параллельные запросы становятся не возможны,
	lock, err := redisSessions.Lock(ctx, curSessionId)
	if err != nil {
		return nil, err
	}
	defer lock.Release(ctx)

	redisToken, err := redisSessions.readToken(ctx, curSessionId)
	if err != nil {
		return nil, err
	}

	// продлеваем access token
	if !redisToken.Token.Valid() {
		// err если refresh token истёк или Keycloak не доступен
		oauthCtx := context.WithValue(ctx, oauth2.HTTPClient, oidcHttpClient)
		tokenSource := oidcProvider.OAuthConfig().TokenSource(oauthCtx, redisToken.Token)
		token, err := tokenSource.Token()
		if err != nil {
			return nil, err
		}
		redisToken = redisSessions.NewRedisSessionToken(token)
	}

	// обновляем токены в Redis всегда, чтобы обновить id сессии
	err = redisSessions.saveToken(ctx, newSessionId, redisToken, curSessionId)
	if err != nil {
		return nil, err
	}

	return redisToken.Token, nil
}
