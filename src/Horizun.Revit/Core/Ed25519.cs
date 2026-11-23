// Ed25519 signature VERIFICATION (RFC 8032, section 5.1.7), for the Pro licence check.
//
// Neither .NET Framework 4.8 (Revit 2023/2024) nor .NET 8 ships Ed25519, and the licence check must not
// pull a cryptography package into every add-in year. This is a direct port of the RFC's reference
// algorithm over System.Numerics.BigInteger: slow (a few milliseconds) and simple, which is the right
// trade for one verification per licence answer. Checked against the RFC 8032 test vectors and against
// signatures made by Node's crypto (tests/Horizun.Core.Tests/Ed25519Tests.cs).
//
// Sign() exists for the tests' fake Hub; production code only verifies.

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Horizun.Revit.Core
{
    public static class Ed25519
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D = Mod(-121665 * Inv(121666));
        private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
        private static readonly Point G = BasePoint();

        private struct Point
        {
            public BigInteger X, Y, Z, T;
            public Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t) { X = x; Y = y; Z = z; T = t; }
        }

        /// <summary>True when <paramref name="signature"/> (64 bytes) is a valid signature of <paramref name="message"/> by <paramref name="publicKey"/> (32 bytes).</summary>
        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            try
            {
                if (publicKey == null || publicKey.Length != 32 || signature == null || signature.Length != 64 || message == null) return false;
                if (!Decompress(publicKey, out Point a)) return false;
                var rs = new byte[32]; Buffer.BlockCopy(signature, 0, rs, 0, 32);
                if (!Decompress(rs, out Point r)) return false;
                var sb = new byte[32]; Buffer.BlockCopy(signature, 32, sb, 0, 32);
                BigInteger s = FromLe(sb);
                if (s >= L) return false;
                BigInteger h = Sha512ModL(rs, publicKey, message);
                return Equal(Mul(s, G), Add(r, Mul(h, a)));
            }
            catch { return false; }
        }

        /// <summary>The public key of a 32-byte seed (tests).</summary>
        public static byte[] PublicKey(byte[] seed)
        {
            Expand(seed, out BigInteger a, out _);
            return Compress(Mul(a, G));
        }

        /// <summary>Signs <paramref name="message"/> with a 32-byte seed (tests).</summary>
        public static byte[] Sign(byte[] seed, byte[] message)
        {
            Expand(seed, out BigInteger a, out byte[] prefix);
            byte[] pub = Compress(Mul(a, G));
            BigInteger r = Sha512ModL(prefix, message);
            byte[] rs = Compress(Mul(r, G));
            BigInteger h = Sha512ModL(rs, pub, message);
            BigInteger s = (r + h * a) % L;
            var sig = new byte[64];
            Buffer.BlockCopy(rs, 0, sig, 0, 32);
            Buffer.BlockCopy(ToLe(s), 0, sig, 32, 32);
            return sig;
        }

        private static void Expand(byte[] seed, out BigInteger a, out byte[] prefix)
        {
            if (seed == null || seed.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            byte[] h;
            using (SHA512 sha = SHA512.Create()) h = sha.ComputeHash(seed);
            var lo = new byte[32]; Buffer.BlockCopy(h, 0, lo, 0, 32);
            a = FromLe(lo);
            a &= (BigInteger.One << 254) - 8;
            a |= BigInteger.One << 254;
            prefix = new byte[32]; Buffer.BlockCopy(h, 32, prefix, 0, 32);
        }

        private static BigInteger Sha512ModL(params byte[][] parts)
        {
            using (SHA512 sha = SHA512.Create())
            {
                for (int i = 0; i < parts.Length - 1; i++) sha.TransformBlock(parts[i], 0, parts[i].Length, null, 0);
                byte[] last = parts[parts.Length - 1];
                sha.TransformFinalBlock(last, 0, last.Length);
                return FromLe(sha.Hash) % L;
            }
        }

        private static Point Add(Point p, Point q)
        {
            BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
            BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
            BigInteger c = Mod(p.T * 2 * D * q.T);
            BigInteger d = Mod(p.Z * 2 * q.Z);
            BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
            return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
        }

        private static Point Mul(BigInteger s, Point p)
        {
            var q = new Point(0, 1, 1, 0);
            while (s > 0)
            {
                if (!s.IsEven) q = Add(q, p);
                p = Add(p, p);
                s >>= 1;
            }
            return q;
        }

        private static bool Equal(Point p, Point q) =>
            Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

        private static bool RecoverX(BigInteger y, int sign, out BigInteger x)
        {
            x = 0;
            if (y >= P) return false;
            BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
            if (x2.IsZero) return sign == 0;
            x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
            if (!Mod(x * x - x2).IsZero) return false;
            if ((int)(x & 1) != sign) x = P - x;
            return true;
        }

        private static byte[] Compress(Point p)
        {
            BigInteger zinv = Inv(p.Z);
            BigInteger x = Mod(p.X * zinv), y = Mod(p.Y * zinv);
            return ToLe(y | ((x & 1) << 255));
        }

        private static bool Decompress(byte[] s, out Point p)
        {
            p = default(Point);
            BigInteger y = FromLe(s);
            int sign = (int)(y >> 255);
            y &= (BigInteger.One << 255) - 1;
            if (!RecoverX(y, sign, out BigInteger x)) return false;
            p = new Point(x, y, 1, Mod(x * y));
            return true;
        }

        private static Point BasePoint()
        {
            BigInteger gy = Mod(4 * Inv(5));
            RecoverX(gy, 0, out BigInteger gx);
            return new Point(gx, gy, 1, Mod(gx * gy));
        }

        private static BigInteger Mod(BigInteger v)
        {
            BigInteger r = BigInteger.Remainder(v, P);
            return r.Sign < 0 ? r + P : r;
        }

        private static BigInteger Inv(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

        private static BigInteger FromLe(byte[] le)
        {
            var b = new byte[le.Length + 1];
            Buffer.BlockCopy(le, 0, b, 0, le.Length);
            return new BigInteger(b);
        }

        private static byte[] ToLe(BigInteger v)
        {
            byte[] raw = v.ToByteArray();
            var r = new byte[32];
            Buffer.BlockCopy(raw, 0, r, 0, Math.Min(32, raw.Length));
            return r;
        }
    }
}
