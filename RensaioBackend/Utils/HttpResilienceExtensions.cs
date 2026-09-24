// The provider resilience implementation (exponential retry + per-host circuit
// breaker) lives in shared/ProviderResilience/ProviderResilience.cs and is linked
// into this project via RensaioBackend.csproj so RensaioBackend and
// RensaioOAuthProxy compile the exact same behaviour. See that file for details.
