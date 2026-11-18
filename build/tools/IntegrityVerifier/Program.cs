using System;
using System.IO;
using StickyNotesPro.Security;

// usage: IntegrityVerifier <file.exe> <pinned-cert-sha256>
// exit code 0 = genuine, 1 = verification failed, 2 = usage error
internal static class Program
{
    private static int Main(string[] a)
    {
        if (a.Length != 2) { Console.Error.WriteLine("usage: IntegrityVerifier <file.exe> <pinned-cert-sha256>"); return 2; }
        var r = AuthenticodeVerifier.Verify(File.ReadAllBytes(a[0]), a[1]);
        Console.WriteLine("file            : " + Path.GetFileName(a[0]));
        Console.WriteLine("file SHA-256    : " + (r.FileSha256 == null ? "-" : AuthenticodeVerifier.Hex(r.FileSha256)));
        Console.WriteLine("authenticode    : " + (r.AuthenticodeDigest == null ? "-" : AuthenticodeVerifier.Hex(r.AuthenticodeDigest)));
        Console.WriteLine("signer          : " + (string.IsNullOrEmpty(r.SignerSubject) ? "-" : r.SignerSubject));
        Console.WriteLine("result          : " + (r.Ok ? "GENUINE" : "FAILED – " + r.Error));
        return r.Ok ? 0 : 1;
    }
}
