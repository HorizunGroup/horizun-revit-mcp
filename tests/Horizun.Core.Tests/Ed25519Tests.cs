using System;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Ed25519Tests
    {
        private static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        // RFC 8032, section 7.1, TEST 1 and TEST 2.
        [Theory]
        [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
                    "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
                    "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
        [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
                    "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
                    "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
        public void Rfc8032_vectors_sign_and_verify(string seed, string pub, string msg, string sig)
        {
            Assert.Equal(Hex(pub), Ed25519.PublicKey(Hex(seed)));
            Assert.Equal(Hex(sig), Ed25519.Sign(Hex(seed), Hex(msg)));
            Assert.True(Ed25519.Verify(Hex(pub), Hex(msg), Hex(sig)));
        }

        [Fact]
        public void A_signature_made_by_node_crypto_verifies()
        {
            // node: crypto.sign(null, Buffer.from('abc'), privateKey) with a freshly generated ed25519 key.
            Assert.True(Ed25519.Verify(
                Hex("3dc261e05ce82511f007a6399498417c9ba6b515a26c74237285c79517e3531a"),
                Encoding.ASCII.GetBytes("abc"),
                Hex("c57e1422f035b1e8f6e571e8408bb00ec55a0cf6407237993420bb3d2f13c15777d1aa5d02871279e9cce9714cbe7e9547dafef4ae51115a88e43a71feb1ad08")));
        }

        [Fact]
        public void Any_changed_bit_fails()
        {
            byte[] seed = Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
            byte[] pub = Ed25519.PublicKey(seed), msg = Encoding.ASCII.GetBytes("licence");
            byte[] sig = Ed25519.Sign(seed, msg);
            Assert.True(Ed25519.Verify(pub, msg, sig));
            byte[] badMsg = Encoding.ASCII.GetBytes("licencE");
            Assert.False(Ed25519.Verify(pub, badMsg, sig));
            for (int i = 0; i < 64; i += 7)
            {
                var s = (byte[])sig.Clone(); s[i] ^= 0x01;
                Assert.False(Ed25519.Verify(pub, msg, s));
            }
            var p = (byte[])pub.Clone(); p[3] ^= 0x10;
            Assert.False(Ed25519.Verify(p, msg, sig));
            Assert.False(Ed25519.Verify(pub, msg, new byte[63]));
            Assert.False(Ed25519.Verify(new byte[31], msg, sig));
        }
    }
}
