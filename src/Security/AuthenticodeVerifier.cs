using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Numerics;

namespace StickyNotesPro.Security
{
    /// <summary>
    /// Self-contained Authenticode verifier (no dependency on the Windows trust store).
    ///
    ///  1. Parses the PE image and recomputes the Authenticode SHA-256 digest
    ///     (whole file except the checksum field, the certificate-table directory entry and the certificate table).
    ///  2. Parses the embedded PKCS#7 SignedData and checks that
    ///       - the signed SpcIndirectDataContent carries exactly that digest,
    ///       - the signed "messageDigest" attribute matches the SpcIndirectDataContent,
    ///       - the RSA signature over the signed attributes is valid,
    ///       - the signing certificate is the PINNED release certificate (SHA-256 of the DER certificate).
    ///  3. Rejects anything appended after the signature, multiple certificate entries, or non-SHA-256 digests.
    /// </summary>
    internal sealed class AuthenticodeResult
    {
        public bool Ok;
        public string Error = "";
        public byte[] FileSha256;
        public byte[] AuthenticodeDigest;
        public byte[] SignerRaw;
        public string SignerSubject = "";
        public string SignerSha256 = "";
    }

    internal static class AuthenticodeVerifier
    {
        private const string OidSignedData = "1.2.840.113549.1.7.2";
        private const string OidSpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
        private const string OidSha256 = "2.16.840.1.101.3.4.2.1";
        private const string OidContentType = "1.2.840.113549.1.9.3";
        private const string OidMessageDigest = "1.2.840.113549.1.9.4";
        private const string OidRsa = "1.2.840.113549.1.1.1";
        private const string OidRsaSha256 = "1.2.840.113549.1.1.11";

        public static AuthenticodeResult Verify(byte[] file, string pinnedCertSha256Hex)
        {
            var r = new AuthenticodeResult();
            try
            {
                using (var sha = SHA256.Create()) r.FileSha256 = sha.ComputeHash(file);

                if (!PeInfo.TryParse(file, out var pe, out string peErr)) return Fail(r, peErr);
                if (pe.CertOffset == 0 || pe.CertSize == 0) return Fail(r, "The executable is not digitally signed.");
                if ((long)pe.CertOffset + pe.CertSize != file.Length) return Fail(r, "Unexpected data after the digital signature (file was modified).");
                if (pe.CertOffset < pe.SizeOfHeaders) return Fail(r, "Certificate table overlaps the PE headers.");

                // WIN_CERTIFICATE
                uint dwLength = BitConverter.ToUInt32(file, (int)pe.CertOffset);
                ushort rev = BitConverter.ToUInt16(file, (int)pe.CertOffset + 4);
                ushort type = BitConverter.ToUInt16(file, (int)pe.CertOffset + 6);
                if (rev != 0x0200 || type != 0x0002) return Fail(r, "Unsupported certificate type.");
                if (dwLength < 8 || dwLength > pe.CertSize || ((dwLength + 7) & ~7u) != pe.CertSize)
                    return Fail(r, "Certificate table contains more than one entry or is malformed.");
                var pkcs7 = new byte[dwLength - 8];
                Buffer.BlockCopy(file, (int)pe.CertOffset + 8, pkcs7, 0, pkcs7.Length);

                // 1. Authenticode digest of the image
                using (var sha = SHA256.Create())
                {
                    sha.TransformBlock(file, 0, pe.ChecksumOffset, null, 0);
                    int a = pe.ChecksumOffset + 4;
                    sha.TransformBlock(file, a, pe.CertDirOffset - a, null, 0);
                    int b = pe.CertDirOffset + 8;
                    sha.TransformFinalBlock(file, b, (int)pe.CertOffset - b);
                    r.AuthenticodeDigest = sha.Hash;
                }

                // 2. PKCS#7
                var ci = Der.Read(pkcs7, 0);
                if (ci.Tag != 0x30) return Fail(r, "Signature is not a PKCS#7 structure.");
                var ciKids = ci.Children(pkcs7);
                if (ciKids.Count < 2 || Der.Oid(pkcs7, ciKids[0]) != OidSignedData) return Fail(r, "Signature is not PKCS#7 SignedData.");
                var sd = ciKids[1].Children(pkcs7)[0];                      // [0] EXPLICIT SignedData
                var sdKids = sd.Children(pkcs7);
                int k = 0;
                k++;                                                        // version
                k++;                                                        // digestAlgorithms
                var encap = sdKids[k++];
                var encapKids = encap.Children(pkcs7);
                if (Der.Oid(pkcs7, encapKids[0]) != OidSpcIndirectData) return Fail(r, "Signature is not an Authenticode signature.");
                var spc = encapKids[1].Children(pkcs7)[0];                  // [0] EXPLICIT SpcIndirectDataContent
                var spcKids = spc.Children(pkcs7);
                var digestInfo = spcKids[1].Children(pkcs7);
                var digAlg = digestInfo[0].Children(pkcs7);
                if (Der.Oid(pkcs7, digAlg[0]) != OidSha256) return Fail(r, "The signature does not use SHA-256.");
                byte[] signedDigest = digestInfo[1].Value(pkcs7);
                if (!FixedEquals(signedDigest, r.AuthenticodeDigest)) return Fail(r, "SHA-256 digest mismatch – the executable was modified after signing.");

                var certs = new List<byte[]>();
                var signerInfos = default(Der.Node);
                for (; k < sdKids.Count; k++)
                {
                    var n = sdKids[k];
                    if (n.Tag == 0xA0) foreach (var c in n.Children(pkcs7)) if (c.Tag == 0x30) certs.Add(c.Raw(pkcs7));
                    if (n.Tag == 0x31) signerInfos = n;
                }
                if (signerInfos.Length == 0) return Fail(r, "No signer information.");
                var signers = signerInfos.Children(pkcs7);
                if (signers.Count != 1) return Fail(r, "Exactly one signer is required.");
                var si = signers[0].Children(pkcs7);
                int j = 0;
                j++;                                                        // version
                var sid = si[j++];                                          // IssuerAndSerialNumber
                var siDigAlg = si[j++].Children(pkcs7);
                if (Der.Oid(pkcs7, siDigAlg[0]) != OidSha256) return Fail(r, "Signer digest is not SHA-256.");
                if (si[j].Tag != 0xA0) return Fail(r, "Signed attributes are missing.");
                var signedAttrs = si[j++];
                var sigAlg = Der.Oid(pkcs7, si[j++].Children(pkcs7)[0]);
                if (sigAlg != OidRsa && sigAlg != OidRsaSha256) return Fail(r, "Unsupported signature algorithm.");
                byte[] signature = si[j].Value(pkcs7);

                byte[] msgDigest = null; string contentType = null;
                foreach (var attr in signedAttrs.Children(pkcs7))
                {
                    var ak = attr.Children(pkcs7);
                    string oid = Der.Oid(pkcs7, ak[0]);
                    var val = ak[1].Children(pkcs7)[0];
                    if (oid == OidMessageDigest) msgDigest = val.Value(pkcs7);
                    else if (oid == OidContentType) contentType = Der.Oid(pkcs7, val);
                }
                if (contentType != OidSpcIndirectData || msgDigest == null) return Fail(r, "Signed attributes are incomplete.");
                using (var sha = SHA256.Create())
                    if (!FixedEquals(msgDigest, sha.ComputeHash(pkcs7, spc.ValueOffset, spc.Length)))
                        return Fail(r, "Signed content digest mismatch.");

                // Pinned signer certificate, parsed straight from DER. No platform crypto APIs are
                // used anywhere in this class, so verification is identical on every runtime and OS
                // (and cannot fail just because a native crypto provider is unavailable).
                byte[] signer = null; CertPub signerPub = new CertPub();
                var sidKids = sid.Children(pkcs7);
                byte[] sidSerial = sidKids[1].Value(pkcs7);
                foreach (var c in certs)
                {
                    if (!string.Equals(Sha256Hex(c), pinnedCertSha256Hex, StringComparison.OrdinalIgnoreCase)) continue;
                    CertPub cp;
                    if (!CertPub.TryParse(c, out cp) || cp.Serial == null) continue;
                    if (FixedEquals(TrimZeros(cp.Serial), TrimZeros(sidSerial))) { signer = c; signerPub = cp; break; }
                }
                if (signer == null) return Fail(r, "The executable is not signed with the official StickyNotes Pro release certificate.");
                if (signerPub.Modulus == null) return Fail(r, "Release certificate does not hold an RSA key.");

                byte[] toVerify = signedAttrs.Raw(pkcs7);
                toVerify[0] = 0x31;                                         // [0] IMPLICIT → SET OF for the signature
                if (!RsaPkcs1Sha256Verify(signerPub.Modulus, signerPub.Exponent, toVerify, signature))
                    return Fail(r, "Digital signature is invalid.");
                r.SignerRaw = signer;
                r.SignerSubject = signerPub.Subject;
                r.SignerSha256 = Sha256Hex(signer);
                r.Ok = true;
                return r;
            }
            catch (Exception ex)
            {
                return Fail(r, "Signature could not be parsed (" + ex.GetType().Name + ").");
            }
        }

        private static AuthenticodeResult Fail(AuthenticodeResult r, string e) { r.Ok = false; r.Error = e; return r; }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        public static string Hex(byte[] b)
        {
            var c = new char[b.Length * 2];
            const string h = "0123456789ABCDEF";
            for (int i = 0; i < b.Length; i++) { c[2 * i] = h[b[i] >> 4]; c[2 * i + 1] = h[b[i] & 15]; }
            return new string(c);
        }

        /// <summary>Constant-time comparison.</summary>
        public static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        private static byte[] TrimZeros(byte[] b)
        {
            int i = 0;
            while (i < b.Length - 1 && b[i] == 0) i++;
            var r = new byte[b.Length - i];
            Buffer.BlockCopy(b, i, r, 0, r.Length);
            return r;
        }

        /// <summary>RSA PKCS#1 v1.5 signature check over SHA-256 – pure managed (System.Numerics).</summary>
        private static bool RsaPkcs1Sha256Verify(byte[] modulus, byte[] exponent, byte[] data, byte[] signature)
        {
            if (modulus == null || exponent == null || signature == null) return false;
            int k = modulus.Length;
            if (k < 64 || signature.Length != k) return false;
            byte[] hash; using (var sha = SHA256.Create()) hash = sha.ComputeHash(data);
            // DigestInfo prefix for SHA-256 (RFC 8017 §9.2)
            byte[] prefix = { 0x30, 0x31, 0x30, 0x0D, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20 };
            int pad = k - prefix.Length - 32 - 3;
            if (pad < 8) return false;
            var em = new byte[k];
            em[1] = 0x01;
            for (int i = 0; i < pad; i++) em[2 + i] = 0xFF;
            int p = 2 + pad; em[p++] = 0x00;
            Buffer.BlockCopy(prefix, 0, em, p, prefix.Length); p += prefix.Length;
            Buffer.BlockCopy(hash, 0, em, p, 32);
            var n = Big(modulus); var e = Big(exponent); var s = Big(signature);
            if (s >= n) return false;
            return FixedEquals(BigBytes(BigInteger.ModPow(s, e, n), k), em);
        }

        /// <summary>Unsigned big-endian integer → BigInteger.</summary>
        private static BigInteger Big(byte[] be)
        {
            var le = new byte[be.Length + 1];
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);      // extra trailing zero byte keeps the value positive
        }

        /// <summary>BigInteger → exactly len big-endian bytes (left-padded with zeros).</summary>
        private static byte[] BigBytes(BigInteger v, int len)
        {
            byte[] le = v.ToByteArray();    // little-endian two's complement
            int n = le.Length;
            while (n > 1 && le[n - 1] == 0) n--;
            var be = new byte[len];
            int c = n < len ? n : len;
            for (int i = 0; i < c; i++) be[len - 1 - i] = le[i];
            return be;
        }

        /// <summary>Serial number, RSA key and subject name parsed directly from the certificate DER.</summary>
        private struct CertPub
        {
            public byte[] Serial, Modulus, Exponent;
            public string Subject;

            public static bool TryParse(byte[] cert, out CertPub cp)
            {
                cp = new CertPub { Subject = "" };
                try
                {
                    var certKids = Der.Read(cert, 0).Children(cert);
                    if (certKids.Count < 1) return false;
                    var tbs = certKids[0].Children(cert);
                    int i = 0;
                    if (tbs[i].Tag == 0xA0) i++;                            // optional version
                    cp.Serial = tbs[i].Value(cert);                         // serialNumber INTEGER
                    cp.Subject = X501Name(cert, tbs[i + 4]);                // signature, issuer, validity, subject
                    var spki = tbs[i + 5].Children(cert);
                    if (Der.Oid(cert, spki[0].Children(cert)[0]) != OidRsa) return true;   // parsed, but not RSA
                    var key = Der.Read(cert, spki[1].ValueOffset + 1);      // skip BIT STRING unused-bits byte
                    var rsaKids = key.Children(cert);
                    cp.Modulus = TrimZeros(rsaKids[0].Value(cert));
                    cp.Exponent = TrimZeros(rsaKids[1].Value(cert));
                    return true;
                }
                catch { return false; }
            }
        }

        private static string X501Name(byte[] b, Der.Node name)
        {
            var parts = new List<string>();
            try
            {
                foreach (var rdn in name.Children(b))
                {
                    if (rdn.Tag != 0x31) continue;
                    foreach (var atav in rdn.Children(b))
                    {
                        var kv = atav.Children(b);
                        if (kv.Count != 2) continue;
                        string oid = Der.Oid(b, kv[0]);
                        string label = oid == "2.5.4.3" ? "CN" : oid == "2.5.4.10" ? "O" : oid == "2.5.4.11" ? "OU" :
                                       oid == "2.5.4.7" ? "L" : oid == "2.5.4.6" ? "C" : oid == "2.5.4.8" ? "S" :
                                       oid == "1.2.840.113549.1.9.1" ? "E" : oid;
                        parts.Add(label + "=" + StringValue(b, kv[1]));
                    }
                }
            }
            catch { }
            return string.Join(", ", parts);
        }

        private static string StringValue(byte[] b, Der.Node n)
        {
            try
            {
                switch (n.Tag)
                {
                    case 0x0C: return System.Text.Encoding.UTF8.GetString(b, n.ValueOffset, n.Length);
                    case 0x1E: return System.Text.Encoding.BigEndianUnicode.GetString(b, n.ValueOffset, n.Length);
                    case 0x13:                                              // PrintableString
                    case 0x14:                                              // TeletexString
                    case 0x16:                                              // IA5String
                        return System.Text.Encoding.ASCII.GetString(b, n.ValueOffset, n.Length);
                }
            }
            catch { }
            return "";
        }
    }

    /// <summary>Minimal PE header parser.</summary>
    internal sealed class PeInfo
    {
        public int PeOffset, ChecksumOffset, CertDirOffset, DataDirOffset, NumberOfRvaAndSizes;
        public uint CertOffset, CertSize, SizeOfHeaders;
        public bool Is64;
        public List<Section> Sections = new List<Section>();

        public struct Section { public string Name; public uint VirtualAddress, VirtualSize, RawPointer, RawSize; }

        public static bool TryParse(byte[] f, out PeInfo pe, out string error)
        {
            pe = null; error = "Not a valid Windows executable.";
            if (f == null || f.Length < 0x200 || f[0] != 'M' || f[1] != 'Z') return false;
            int peOff = BitConverter.ToInt32(f, 0x3C);
            if (peOff <= 0 || peOff > f.Length - 0x200) return false;
            if (f[peOff] != 'P' || f[peOff + 1] != 'E' || f[peOff + 2] != 0 || f[peOff + 3] != 0) return false;
            int nSections = BitConverter.ToUInt16(f, peOff + 6);
            int optSize = BitConverter.ToUInt16(f, peOff + 20);
            int opt = peOff + 24;
            ushort magic = BitConverter.ToUInt16(f, opt);
            if (magic != 0x10B && magic != 0x20B) return false;
            var p = new PeInfo { PeOffset = peOff, Is64 = magic == 0x20B };
            p.ChecksumOffset = opt + 64;
            p.SizeOfHeaders = BitConverter.ToUInt32(f, opt + 60);
            p.NumberOfRvaAndSizes = BitConverter.ToInt32(f, opt + (p.Is64 ? 108 : 92));
            p.DataDirOffset = opt + (p.Is64 ? 112 : 96);
            if (p.NumberOfRvaAndSizes < 5) return false;
            p.CertDirOffset = p.DataDirOffset + 4 * 8;
            p.CertOffset = BitConverter.ToUInt32(f, p.CertDirOffset);
            p.CertSize = BitConverter.ToUInt32(f, p.CertDirOffset + 4);
            int sec = opt + optSize;
            if (sec + nSections * 40 > f.Length) return false;
            for (int i = 0; i < nSections; i++)
            {
                int o = sec + i * 40;
                p.Sections.Add(new Section
                {
                    Name = System.Text.Encoding.ASCII.GetString(f, o, 8).TrimEnd('\0'),
                    VirtualSize = BitConverter.ToUInt32(f, o + 8),
                    VirtualAddress = BitConverter.ToUInt32(f, o + 12),
                    RawSize = BitConverter.ToUInt32(f, o + 16),
                    RawPointer = BitConverter.ToUInt32(f, o + 20),
                });
            }
            pe = p; error = "";
            return true;
        }

        public void DataDir(byte[] f, int index, out uint rva, out uint size)
        {
            rva = 0; size = 0;
            if (index >= NumberOfRvaAndSizes) return;
            rva = BitConverter.ToUInt32(f, DataDirOffset + index * 8);
            size = BitConverter.ToUInt32(f, DataDirOffset + index * 8 + 4);
        }

        /// <summary>RVA → file offset (-1 when not backed by file data).</summary>
        public long RvaToOffset(uint rva)
        {
            foreach (var s in Sections)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize))
                {
                    uint delta = rva - s.VirtualAddress;
                    return delta < s.RawSize ? s.RawPointer + delta : -1;
                }
            return -1;
        }
    }

    /// <summary>Tiny DER reader (definite lengths only – DER never uses indefinite lengths).</summary>
    internal static class Der
    {
        public struct Node
        {
            public int Tag, Offset, ValueOffset, Length;
            public int End => ValueOffset + Length;
            public byte[] Value(byte[] b) { var r = new byte[Length]; Buffer.BlockCopy(b, ValueOffset, r, 0, Length); return r; }
            public byte[] Raw(byte[] b) { var r = new byte[End - Offset]; Buffer.BlockCopy(b, Offset, r, 0, r.Length); return r; }
            public List<Node> Children(byte[] b)
            {
                var list = new List<Node>();
                int p = ValueOffset;
                while (p < End)
                {
                    var n = Read(b, p);
                    if (n.End > End) throw new InvalidDataException("DER child overruns parent");
                    list.Add(n);
                    p = n.End;
                    if (list.Count > 100000) throw new InvalidDataException("DER too many children");
                }
                return list;
            }
        }

        public static Node Read(byte[] b, int p)
        {
            var n = new Node { Offset = p, Tag = b[p] };
            if ((b[p] & 0x1F) == 0x1F) throw new InvalidDataException("High tag numbers not supported");
            int q = p + 1;
            int len = b[q++];
            if (len == 0x80) throw new InvalidDataException("Indefinite length");
            if (len > 0x80)
            {
                int cnt = len & 0x7F;
                if (cnt > 4) throw new InvalidDataException("Length too large");
                len = 0;
                for (int i = 0; i < cnt; i++) len = (len << 8) | b[q++];
                if (len < 0) throw new InvalidDataException("Negative length");
            }
            n.ValueOffset = q; n.Length = len;
            if ((long)q + len > b.Length) throw new InvalidDataException("DER overruns buffer");
            return n;
        }

        public static string Oid(byte[] b, Node n)
        {
            if (n.Tag != 0x06) return "";
            var sb = new System.Text.StringBuilder();
            long v = 0; bool first = true;
            for (int i = n.ValueOffset; i < n.End; i++)
            {
                v = (v << 7) | (uint)(b[i] & 0x7F);
                if ((b[i] & 0x80) != 0) continue;
                if (first) { long x = v < 80 ? v / 40 : 2; sb.Append(x).Append('.').Append(v - 40 * x); first = false; }
                else sb.Append('.').Append(v);
                v = 0;
            }
            return sb.ToString();
        }
    }
}
