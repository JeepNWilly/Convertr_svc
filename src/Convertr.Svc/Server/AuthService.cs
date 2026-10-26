// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Convertr.Svc.Server;

public sealed class AuthState
{
    public string? PasswordHash { get; set; }
    public string SessionKey { get; set; } = "";
    /// <summary>Bumped on every password change or reset: all earlier session cookies stop working.</summary>
    public long Epoch { get; set; } = 1;
    public string? SetupTokenHash { get; set; }
    public DateTime? SetupTokenExpiresUtc { get; set; }
}

public sealed record Session(string Id, DateTime ExpiresUtc, long Epoch);

public enum LoginResult { Ok, BadCredentials, LockedOut, NeedsSetup }

/// <summary>
/// Single admin password (PBKDF2-SHA256, per-password salt), first-run setup token, signed stateless session cookies,
/// per-session CSRF token, login rate limiting with exponential back-off. State lives in auth.json (0600) in the data dir.
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "convertr_session";
    private readonly ServerOptions _o;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private AuthState _state = new();
    private DateTime _loadedWrite = DateTime.MinValue;
    private readonly HashSet<string> _revoked = new();
    private readonly Dictionary<string, (int Fails, DateTime LockedUntil)> _fails = new();
    private readonly Queue<DateTime> _globalFails = new();

    public AuthService(ServerOptions o, TimeProvider? time = null) { _o = o; _time = time ?? TimeProvider.System; Reload(true); }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---------- state file ----------
    private void Reload(bool force = false)
    {
        lock (_lock)
        {
            var path = _o.AuthPath;
            var wt = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (!force && wt == _loadedWrite) return;
            AuthState? st = null;
            try { if (File.Exists(path)) st = JsonSerializer.Deserialize<AuthState>(File.ReadAllText(path), Json); } catch { /* damaged file: start fresh (the password must then be set again) */ }
            if (st == null || string.IsNullOrEmpty(st.SessionKey))
            {
                st ??= new AuthState();
                st.SessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                _state = st; Save();
                return;
            }
            _state = st; _loadedWrite = wt;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_o.DataDir);
        var path = _o.AuthPath;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, true);
        _loadedWrite = File.GetLastWriteTimeUtc(path);
    }

    public bool NeedsSetup { get { Reload(); lock (_lock) return string.IsNullOrEmpty(_state.PasswordHash); } }

    // ---------- setup token ----------
    /// <summary>Creates a one-time setup token when no password exists. Returns the plain token (only ever shown in the log / CLI), or null when not needed.</summary>
    public string? EnsureSetupToken(bool forceNew = false)
    {
        Reload();
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_state.PasswordHash)) return null;
            if (!forceNew && _state.SetupTokenHash != null && _state.SetupTokenExpiresUtc > Now) return null;   // a live token exists; its plain text was shown when created
            var token = Base64Url(RandomNumberGenerator.GetBytes(18));
            _state.SetupTokenHash = Sha256(token);
            _state.SetupTokenExpiresUtc = Now.AddHours(24);
            Save();
            return token;
        }
    }

    /// <summary>A new token on every start while no password is set, so the log always shows a usable one.</summary>
    public string? NewSetupTokenForStartup() => EnsureSetupToken(forceNew: true);

    public (bool Ok, string? Error, int RetryAfter) Setup(string ip, string token, string password)
    {
        Reload();
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_state.PasswordHash)) return (false, "a password is already set", 0);
            if (Locked("setup:" + ip, out var wait)) return (false, "too many attempts", wait);
            if (password.Length < _o.MinPasswordLength) return (false, $"the password must have at least {_o.MinPasswordLength} characters", 0);
            var ok = _state.SetupTokenHash != null && _state.SetupTokenExpiresUtc > Now && FixedEquals(Sha256(token ?? ""), _state.SetupTokenHash);
            if (!ok) { Fail("setup:" + ip); return (false, "the setup token is not valid", 0); }
            _state.PasswordHash = Hash(password);
            _state.SetupTokenHash = null; _state.SetupTokenExpiresUtc = null;
            _state.Epoch++;
            Save();
            _fails.Remove("setup:" + ip);
            return (true, null, 0);
        }
    }

    /// <summary>--reset-password: removes the password and makes a fresh setup token. Existing sessions stop working.</summary>
    public string ResetPassword()
    {
        Reload();
        lock (_lock)
        {
            _state.PasswordHash = null;
            _state.Epoch++;
            _state.SetupTokenHash = null;
            _state.SetupTokenExpiresUtc = null;
            Save();
        }
        return EnsureSetupToken(forceNew: true)!;
    }

    public (bool Ok, string? Error) ChangePassword(string current, string next)
    {
        Reload();
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_state.PasswordHash) || !Verify(current, _state.PasswordHash)) return (false, "the current password is not correct");
            if (next.Length < _o.MinPasswordLength) return (false, $"the password must have at least {_o.MinPasswordLength} characters");
            _state.PasswordHash = Hash(next); _state.Epoch++; Save();
            return (true, null);
        }
    }

    // ---------- login ----------
    public (LoginResult Result, string? Cookie, int RetryAfter) Login(string ip, string password)
    {
        Reload();
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_state.PasswordHash)) return (LoginResult.NeedsSetup, null, 0);
            if (Locked("login:" + ip, out var wait) || GlobalLocked(out var gw) && (wait = gw) > 0) return (LoginResult.LockedOut, null, Math.Max(wait, 1));
            if (!Verify(password ?? "", _state.PasswordHash)) { Fail("login:" + ip); return (LoginResult.BadCredentials, null, 0); }
            _fails.Remove("login:" + ip);
            return (LoginResult.Ok, MakeCookie(Base64Url(RandomNumberGenerator.GetBytes(12)), Now.AddHours(_o.SessionHours)), 0);
        }
    }

    private bool Locked(string key, out int wait)
    {
        wait = 0;
        if (_fails.TryGetValue(key, out var f) && f.LockedUntil > Now) { wait = (int)Math.Ceiling((f.LockedUntil - Now).TotalSeconds); return true; }
        return false;
    }

    private void Fail(string key)
    {
        _fails.TryGetValue(key, out var f);
        var n = f.Fails + 1;
        // 5 free attempts, then 2, 4, 8 ... seconds up to 15 minutes
        var delay = n <= 5 ? 0 : Math.Min(900, (int)Math.Pow(2, n - 5));
        _fails[key] = (n, delay > 0 ? Now.AddSeconds(delay) : DateTime.MinValue);
        _globalFails.Enqueue(Now);
    }

    private bool GlobalLocked(out int wait)
    {
        wait = 0;
        while (_globalFails.Count > 0 && _globalFails.Peek() < Now.AddMinutes(-1)) _globalFails.Dequeue();
        if (_globalFails.Count > 40) { wait = 30; return true; }   // many failures from many addresses
        return false;
    }

    // ---------- sessions ----------
    private string MakeCookie(string id, DateTime expires)
    {
        var payload = $"v1.{id}.{new DateTimeOffset(expires).ToUnixTimeSeconds()}.{_state.Epoch}";
        return payload + "." + Sign(payload);
    }

    public Session? Validate(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        Reload();
        lock (_lock)
        {
            var i = cookie.LastIndexOf('.');
            if (i < 0) return null;
            var payload = cookie[..i];
            if (!FixedEquals(Sign(payload), cookie[(i + 1)..])) return null;
            var parts = payload.Split('.');
            if (parts.Length != 4 || parts[0] != "v1" || !long.TryParse(parts[2], out var exp) || !long.TryParse(parts[3], out var epoch)) return null;
            var expires = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
            if (expires <= Now || epoch != _state.Epoch || string.IsNullOrEmpty(_state.PasswordHash) || _revoked.Contains(parts[1])) return null;
            return new Session(parts[1], expires, epoch);
        }
    }

    /// <summary>A fresh cookie for a session that is more than an hour old (sliding expiry), or null.</summary>
    public string? Renew(Session s)
    {
        var fresh = Now.AddHours(_o.SessionHours);
        if ((fresh - s.ExpiresUtc).TotalMinutes < 60) return null;
        lock (_lock) return MakeCookie(s.Id, fresh);
    }

    public void Logout(Session s) { lock (_lock) _revoked.Add(s.Id); }

    public string CsrfFor(Session s) { lock (_lock) return Sign("csrf:" + s.Id)[..32]; }
    public bool CsrfOk(Session s, string? header) => !string.IsNullOrEmpty(header) && FixedEquals(header, CsrfFor(s));

    // ---------- crypto ----------
    private string Sign(string text)
    {
        var key = Convert.FromBase64String(_state.SessionKey);
        return Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _o.PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${_o.PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out var iter) || iter < 1000) return false;
        try
        {
            var salt = Convert.FromBase64String(p[2]); var expected = Convert.FromBase64String(p[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
