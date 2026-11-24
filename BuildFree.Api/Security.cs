using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BuildFree.Api;

public record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);
public record PasswordResetRequest(string? Code, string? Password);

public static class Passwords
{
    // BCrypt only uses the first 72 bytes of a password, so longer ones are
    // refused instead of being cut short without anyone knowing.
    public const int MaxBytes = 72;

    public static string? Problem(string? password)
    {
        if (password is null || password.Length < 8) return "Password must be at least 8 characters.";
        if (Encoding.UTF8.GetByteCount(password) > MaxBytes) return "Password is too long (72 characters at most).";
        return null;
    }

    // A short fingerprint of the stored password hash. It goes into every login
    // token and reset link, so changing a password signs out every other device
    // and makes older reset links stop working.
    public static string Stamp(string passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];
}

// Slows down password guessing: after 5 wrong passwords for one email within
// 15 minutes, logins for that email are refused until the 15 minutes are up.
// It's kept in memory, so it resets when the API restarts.
public static class LoginGuard
{
    const int MaxFailures = 5;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    static readonly ConcurrentDictionary<string, (int Failures, DateTime First)> failures = new();

    // Minutes left before this email can try again, or null if it can try now.
    public static int? MinutesLocked(string email)
    {
        if (!failures.TryGetValue(email, out var f)) return null;
        var left = f.First + Window - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            failures.TryRemove(email, out _);
            return null;
        }
        return f.Failures >= MaxFailures ? (int)Math.Ceiling(left.TotalMinutes) : null;
    }

    public static void Failed(string email)
    {
        var now = DateTime.UtcNow;
        failures.AddOrUpdate(email, _ => (1, now),
            (_, f) => f.First + Window <= now ? (1, now) : (f.Failures + 1, f.First));
        // Forget old entries now and then, so the list can't grow without limit.
        if (failures.Count > 10_000)
            foreach (var (key, f) in failures)
                if (f.First + Window <= now) failures.TryRemove(key, out _);
    }

    public static void Clear(string email) => failures.TryRemove(email, out _);
}

// Password reset links. A code holds the user's id and an expiry time, signed
// with the server's key together with the user's password stamp. Nothing is
// stored: a code stops working when it expires or as soon as the password
// changes, so each link works once.
public static class ResetCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static string Make(byte[] serverKey, User u, DateTime expiresAt)
    {
        var payload = new byte[24];
        u.Id.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), new DateTimeOffset(expiresAt, TimeSpan.Zero).ToUnixTimeSeconds());
        var code = payload.Concat(Sign(serverKey, payload, u)).ToArray();
        return Convert.ToBase64String(code).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // The user a code is for, if it's well formed and not expired. Call IsFor
    // afterwards to be sure it's genuine and hasn't been used.
    public static Guid? UserId(string? code)
    {
        var bytes = Decode(code);
        if (bytes is null) return null;
        var seconds = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(16, 8));
        if (seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static bool IsFor(byte[] serverKey, string code, User u)
    {
        var bytes = Decode(code);
        if (bytes is null) return false;
        return CryptographicOperations.FixedTimeEquals(Sign(serverKey, bytes[..24], u), bytes.AsSpan(24));
    }

    static byte[] Sign(byte[] serverKey, byte[] payload, User u)
    {
        // A key just for reset links, so a reset code can never pass as anything else.
        var key = HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes("BuildFree password reset"));
        return HMACSHA256.HashData(key, payload.Concat(Encoding.UTF8.GetBytes(Passwords.Stamp(u.PasswordHash))).ToArray());
    }

    static byte[]? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100) return null;
        var s = code.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(s);
            return bytes.Length == 24 + 32 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
}
