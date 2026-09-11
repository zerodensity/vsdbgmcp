using System;
using System.Security.Cryptography;

namespace VsDbgMcp
{
    /// <summary>Opaque, filename-safe handles for tool calls. Never use these as authentication tokens.</summary>
    public static class ShortId
    {
        const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        public const int Length = 12;

        public static string New(Func<string, bool> inUse = null)
        {
            // 60 random bits, lowercase only so Windows paths cannot alias by case.
            // Owners reject collisions with retained identities before issuing a handle.
            using (var random = RandomNumberGenerator.Create())
            {
                var bytes = new byte[Length];
                var chars = new char[Length];
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    random.GetBytes(bytes);
                    for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[bytes[i] & 31];
                    var id = new string(chars);
                    if (inUse == null || !inUse(id)) return id;
                }
            }
            throw new InvalidOperationException("Could not allocate an unused ID.");
        }

        public static bool IsValid(string id)
        {
            if (id == null || id.Length != Length) return false;
            foreach (var c in id) if (Alphabet.IndexOf(c) < 0) return false;
            return true;
        }

        public static bool IsCaptureId(string id) => IsValid(id) || Guid.TryParseExact(id, "N", out _);
    }
}
