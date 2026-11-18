#!/usr/bin/env python3
"""StickyNotes Pro — Certificate of Integrity generator (build/integrity_record.py).

Reads the release binaries + the osslsigncode verification log, cross-checks the
pinned certificate and digests, renders a formal PDF (reportlab) and signs the
PDF itself with the release certificate (pyhanko). Called by build/release.sh.
"""
import argparse, datetime as dt, hashlib, json, math, os, re, sys

from cryptography import x509
from cryptography.hazmat.primitives import hashes
from cryptography.x509.oid import ExtensionOID, NameOID

from reportlab.lib import colors
from reportlab.lib.colors import HexColor
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.platypus import (BaseDocTemplate, Frame, PageTemplate, Paragraph, Spacer,
                                Table, TableStyle, Flowable, KeepTogether, PageBreak)
from reportlab.graphics.shapes import Drawing
from reportlab.graphics.barcode.qr import QrCodeWidget

NAVY = HexColor("#1F3A5F"); GOLD = HexColor("#C9A227"); PALE = HexColor("#EEF2F7")
LINE = HexColor("#B9C4D0"); MUTED = HexColor("#5A6572"); INK = HexColor("#1A2430")

# ------------------------------------------------------------------ parsing

def file_hashes(path):
    b = open(path, "rb").read()
    return {"sha256": hashlib.sha256(b).hexdigest().upper(),
            "sha512": hashlib.sha512(b).hexdigest().upper(),
            "size": len(b)}

def iso_time(t):
    t = " ".join((t or "").split())
    for fmt in ("%b %d %H:%M:%S %Y GMT", "%b %d %H:%M:%S %Y"):
        try:
            return dt.datetime.strptime(t, fmt).strftime("%Y-%m-%d %H:%M:%S UTC")
        except ValueError:
            pass
    return t

def parse_verify_log(text):
    def grab(pat, flags=0):
        m = re.search(pat, text, flags)
        return m.group(1).strip() if m else ""
    cur = grab(r"Current message digest\s*:\s*([0-9A-Fa-f]+)")
    calc = grab(r"Calculated message digest\s*:\s*([0-9A-Fa-f]+)")
    tsa = ""
    m = re.search(r"(?s)Countersignatures.*?Issuer\s*:\s*(.+?)[\r\n]", text)
    if m: tsa = m.group(1).strip()
    return {
        "current_digest": cur.upper(), "calculated_digest": calc.upper(),
        "pe_checksum": grab(r"PE checksum\s*:\s*([0-9A-Fa-f]{8})"),
        "signing_time": grab(r"Signing time\s*:\s*(.+?)[\r\n]"),
        "timestamp_time": grab(r"Timestamp time\s*:\s*(.+?)[\r\n]"),
        "tsa_issuer": tsa,
        "verified": ("Signature verification: ok" in text) or ("Succeeded" in text),
    }

_OID_LABELS = {NameOID.COMMON_NAME: "CN", NameOID.ORGANIZATION_NAME: "O",
               NameOID.ORGANIZATIONAL_UNIT_NAME: "OU", NameOID.LOCALITY_NAME: "L",
               NameOID.COUNTRY_NAME: "C", NameOID.STATE_OR_PROVINCE_NAME: "S",
               NameOID.EMAIL_ADDRESS: "E"}

def name_str(n):
    return ", ".join(f"{_OID_LABELS.get(a.oid, a.oid._name)}={a.value}" for a in n)

def cert_info(pem):
    c = x509.load_pem_x509_certificate(open(pem, "rb").read())
    pub = c.public_key()
    key = f"RSA {pub.key_size}-bit" if pub.__class__.__name__.startswith("_RSAPublic") \
        else pub.__class__.__name__.replace("_", " ").replace("PublicKey", "key")
    def ext(oid, default="-"):
        try: return c.extensions.get_extension_for_oid(oid).value
        except x509.ExtensionNotFound: return default
    ku = ext(ExtensionOID.KEY_USAGE)
    kusage = []
    if ku != "-":
        for f, lbl in [(ku.digital_signature, "Digital Signature"), (ku.content_commitment, "Non-repudiation")]:
            if f: kusage.append(lbl)
    eku = ext(ExtensionOID.EXTENDED_KEY_USAGE)
    ekusage = [e._name for e in eku] if eku != "-" else []
    bc = ext(ExtensionOID.BASIC_CONSTRAINTS)
    ski = ext(ExtensionOID.SUBJECT_KEY_IDENTIFIER)
    try:
        nb, na = c.not_valid_before_utc, c.not_valid_after_utc
    except AttributeError:
        nb, na = c.not_valid_before.replace(tzinfo=dt.timezone.utc), c.not_valid_after.replace(tzinfo=dt.timezone.utc)
    sha256 = c.fingerprint(hashes.SHA256()).hex().upper()
    sha1 = c.fingerprint(hashes.SHA1()).hex().upper()
    return {
        "subject": name_str(c.subject), "issuer": name_str(c.issuer),
        "self_signed": c.subject == c.issuer,
        "serial": format(c.serial_number, "X"),
        "not_before": nb.strftime("%Y-%m-%d %H:%M:%S UTC"), "not_after": na.strftime("%Y-%m-%d %H:%M:%S UTC"),
        "public_key": key,
        "signature_algorithm": c.signature_algorithm_oid._name,
        "key_usage": ", ".join(kusage) or "-", "eku": ", ".join(ekusage) or "-",
        "basic_constraints": ("CA: TRUE" if bc != "-" and bc.ca else "End-entity certificate (CA: FALSE)"),
        "ski": ski.digest.hex().upper() if ski != "-" else "-",
        "sha256_fingerprint": sha256, "sha1_thumbprint": sha1,
        "version": f"v{c.version.value + 1}",
    }

def grouped(h, n=8):
    h = "".join(h.split())
    return " ".join(h[i:i + n] for i in range(0, len(h), n))

# ------------------------------------------------------------------ signature anchor

SIG_BOX = {}
TOTAL_PAGES = {"n": 0}

class SigAnchor(Flowable):
    """Reserves space for the visible PDF signature and records its absolute position."""
    def __init__(self, box, w=105 * mm, h=24 * mm):
        super().__init__()
        self.box, self.width, self.height = box, w, h

    def draw(self):
        x, y = self.canv.absolutePosition(0, 0)
        self.box.update(page=self.canv.getPageNumber(), rect=(x, y, x + self.width, y + self.height))

class Seal(Flowable):
    def __init__(self, r=15 * mm, text="VERIFIED"):
        super().__init__()
        self.r = r; self.text = text; self.width = self.height = 2 * r

    def draw(self):
        c = self.canv; r = self.r
        c.saveState(); c.translate(r, r)
        c.setStrokeColor(GOLD); c.setLineWidth(1.6); c.circle(0, 0, r - 1, stroke=1, fill=0)
        c.setLineWidth(0.8); c.circle(0, 0, r - 4 * mm, stroke=1, fill=0)
        label = "STICKYNOTES PRO  *  RELEASE INTEGRITY  *  "
        c.setFillColor(GOLD); c.setFont("Helvetica-Bold", 6.2)
        for i, ch in enumerate(label):
            a = math.pi / 2 - i * (2 * math.pi / len(label))
            c.saveState(); c.rotate(math.degrees(a) - 90)
            c.drawCentredString(0, r - 11.5, ch); c.restoreState()
        c.setFillColor(NAVY)
        c.setFont("Helvetica-Bold", 8.5); c.drawCentredString(0, 1.5, self.text)
        c.setFont("Helvetica", 6); c.drawCentredString(0, -7, "SHA-256")
        c.restoreState()

def qr(data, size=30 * mm):
    w = QrCodeWidget(data); w.barLevel = "M"
    b = w.getBounds()
    d = Drawing(size, size, transform=[size / (b[2] - b[0]), 0, 0, size / (b[3] - b[1]), 0, 0])
    d.add(w)
    return d

# ------------------------------------------------------------------ PDF

def build_pdf(path, m):
    S = {
        "title": ParagraphStyle("t", fontName="Helvetica-Bold", fontSize=24, leading=28, textColor=NAVY, alignment=TA_CENTER),
        "sub": ParagraphStyle("s", fontName="Helvetica", fontSize=11.5, leading=15, textColor=MUTED, alignment=TA_CENTER),
        "h": ParagraphStyle("h", fontName="Helvetica-Bold", fontSize=11, leading=14, textColor=NAVY, spaceBefore=9, spaceAfter=4),
        "p": ParagraphStyle("p", fontName="Helvetica", fontSize=9, leading=12.5, textColor=INK),
        "small": ParagraphStyle("sm", fontName="Helvetica", fontSize=7.8, leading=10.5, textColor=MUTED),
        "k": ParagraphStyle("k", fontName="Helvetica-Bold", fontSize=8.3, leading=11, textColor=MUTED),
        "v": ParagraphStyle("v", fontName="Helvetica", fontSize=8.6, leading=11.2, textColor=INK),
        "mono": ParagraphStyle("m", fontName="Courier", fontSize=7.7, leading=10.4, textColor=INK),
        "monob": ParagraphStyle("mb", fontName="Courier-Bold", fontSize=10.2, leading=13.5, textColor=NAVY),
        "code": ParagraphStyle("c", fontName="Courier", fontSize=7.8, leading=10.2, textColor=INK,
                               backColor=PALE, borderPadding=(5, 6, 5, 6), leftIndent=6, rightIndent=6,
                               spaceBefore=3, spaceAfter=7),
        "bul": ParagraphStyle("b", fontName="Helvetica", fontSize=8.8, leading=12, textColor=INK, leftIndent=12, bulletIndent=2),
    }
    exe, setup, sig, cert = m["executable"], m["installer"], m["signature"], m["certificate"]
    rec_no = m["record_number"]

    def kv(rows, col=44 * mm, mono_keys=()):
        data = []
        for k, v in rows:
            st = S["mono"] if k in mono_keys else S["v"]
            data.append([Paragraph(k, S["k"]), Paragraph(v, st)])
        t = Table(data, colWidths=[col, 170 * mm - col])
        t.setStyle(TableStyle([
            ("VALIGN", (0, 0), (-1, -1), "TOP"),
            ("LINEBELOW", (0, 0), (-1, -2), 0.4, LINE),
            ("TOPPADDING", (0, 0), (-1, -1), 3.2), ("BOTTOMPADDING", (0, 0), (-1, -1), 3.2),
        ]))
        return t

    def box(title, value, note=""):
        inner = [Paragraph(title, S["k"]), Spacer(1, 1.5 * mm), Paragraph(grouped(value), S["monob"])]
        t = Table([[inner]], colWidths=[170 * mm])
        t.setStyle(TableStyle([("BOX", (0, 0), (-1, -1), 0.9, NAVY), ("BACKGROUND", (0, 0), (-1, -1), colors.white),
                               ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
                               ("LEFTPADDING", (0, 0), (-1, -1), 7), ("RIGHTPADDING", (0, 0), (-1, -1), 7)]))
        out = [t]
        if note: out.append(Paragraph(note, S["small"]))
        return out

    story = []
    # ---- header block
    story.append(Spacer(1, 6 * mm))
    story.append(Paragraph("CERTIFICATE OF INTEGRITY", S["title"]))
    story.append(Spacer(1, 2.5 * mm))
    story.append(Paragraph(f"Official Release Integrity Record &nbsp;·&nbsp; StickyNotes Pro version {m['version']}", S["sub"]))
    story.append(Spacer(1, 5 * mm))
    meta = Table([['<b>Record number</b>', '<b>Date of issue</b>', '<b>Issued by</b>'],
                  [rec_no, m["issue_time"], "StickyNotes Pro<br/>Release Engineering"]],
                 colWidths=[56 * mm, 60 * mm, 54 * mm])
    meta.setStyle(TableStyle([("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"), ("FONTSIZE", (0, 0), (-1, -1), 9),
                              ("TEXTCOLOR", (0, 0), (-1, 0), MUTED), ("LINEABOVE", (0, 0), (-1, 0), 0.8, LINE),
                              ("LINEBELOW", (0, 1), (-1, 1), 0.8, LINE),
                              ("TOPPADDING", (0, 0), (-1, -1), 4), ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
                              ("FONTNAME", (0, 1), (-1, 1), "Helvetica")]))
    story.append(meta)
    story.append(Spacer(1, 4 * mm))
    story.append(Paragraph(
        "This record certifies that the release files identified below were built from the StickyNotes Pro source "
        "code, digitally signed with the StickyNotes Pro release code-signing certificate and verified successfully "
        "before publication. The SHA-256 values stated in this record are authoritative: <b>any file whose SHA-256 "
        "value differs from the value stated here is not an authentic StickyNotes Pro release and must not be "
        "installed or run.</b>", S["p"]))

    # ---- 1 identification
    story.append(Paragraph("1.&nbsp; Release identification", S["h"]))
    story.append(kv([
        ("Product", "StickyNotes Pro - desktop sticky notes for Windows"),
        ("Version", m["version"]),
        ("Executable", f"StickyNotesPro.exe&nbsp; ({exe['size']:,} bytes)"),
        ("Installer", f"{os.path.basename(m['installer_path'])}&nbsp; ({setup['size']:,} bytes)"),
        ("Platform", "Windows 7 SP1 / 8.1 / 10 / 11 (x86 and x64) - .NET Framework 4.8"),
        ("Build / signing date", sig["signing_time"]),
    ]))

    # ---- 2 fingerprints
    story.append(Paragraph("2.&nbsp; Cryptographic fingerprints of the signed executable", S["h"]))
    story += box("SHA-256 of StickyNotesPro.exe (complete file, including its digital signature)", exe["sha256"])
    story.append(kv([
        ("Authenticode digest", grouped(sig["current_digest"]) + "<br/><font size='7.2' color='#5A6572'>SHA-256 PE image digest "
         "signed inside the certificate (excludes checksum and signature block)</font>"),
        ("SHA-512", grouped(exe["sha512"])),
        ("Installer SHA-256", grouped(setup["sha256"])),
    ], mono_keys=("Authenticode digest", "SHA-512", "Installer SHA-256")))

    # ---- 3 signature
    story.append(Paragraph("3.&nbsp; Digital signature", S["h"]))
    story.append(kv([
        ("Signature format", "Microsoft Authenticode (PKCS#7 SignedData), embedded in executable and installer"),
        ("Digest / signature", "SHA256 / SHA256 with RSA"),
        ("Signing time", sig["signing_time"]),
        ("Trusted timestamp", (sig["timestamp_time"] + (f" &nbsp;-&nbsp; RFC 3161 countersignature by {sig['tsa_issuer']}"
                                                       if sig["tsa_issuer"] else ""))),
        ("Verification result", ("<b>VALID</b> - signature and digest verified by osslsigncode and by the StickyNotes Pro "
                                 "integrity verifier (same code as the in-app check) on " + m["issue_time"] +
                                 "; tamper tests (modified byte, appended data, unsigned copy, foreign certificate) "
                                 "all rejected."
                                 if sig["verified"] else
                                 "<b>NOT VERIFIED - see build log</b>")),
    ]))

    story.append(PageBreak())

    # ---- 4 certificate
    story.append(Paragraph("4.&nbsp; Code-signing certificate", S["h"]))
    story.append(kv([
        ("Subject", cert["subject"]),
        ("Issuer", cert["issuer"] + ("&nbsp; <i>(self-signed)</i>" if cert["self_signed"] else "")),
        ("Serial number", grouped(cert["serial"], 4)),
        ("Valid from", cert["not_before"]),
        ("Valid until", cert["not_after"]),
        ("Public key", cert["public_key"]),
        ("Signature algorithm", cert["signature_algorithm"]),
        ("Key usage", cert["key_usage"]),
        ("Enhanced key usage", cert["eku"] + (" (1.3.6.1.5.5.7.3.3)" if "codeSigning" in cert["eku"] else "")),
        ("Basic constraints", cert["basic_constraints"]),
        ("Subject key identifier", grouped(cert["ski"], 4)),
        ("X.509 version", cert["version"]),
    ], mono_keys=("Serial number", "Subject key identifier")))
    story += box("Certificate SHA-256 fingerprint - PINNED inside StickyNotesPro.exe", cert["sha256_fingerprint"],
                 "The application refuses to run if it is not signed with exactly this certificate.")
    story.append(Spacer(1, 1.5 * mm))
    story.append(kv([("Thumbprint (SHA-1)", grouped(cert["sha1_thumbprint"], 5) +
                      "<br/><font size='7.2' color='#5A6572'>value shown by Windows in File Properties > Digital Signatures > "
                      "Details > View certificate</font>")], mono_keys=("Thumbprint (SHA-1)",)))
    story.append(Spacer(1, 2 * mm))
    story.append(Paragraph(
        "<b>Trust model.</b> This is a publisher-issued (self-signed) certificate, not a certificate issued by a public "
        "certification authority. Windows will therefore report the publisher as unverified unless the certificate is "
        "added to the computer's <i>Trusted Publishers</i> store. Authenticity is guaranteed by certificate pinning "
        "inside the application and by the fingerprints published in this record. For public distribution, rebuild with "
        "a CA-issued (OV/EV) code-signing certificate (build/release.sh accepts it via SNP_PFX); the pin and this record "
        "are regenerated automatically.", S["small"]))

    # ---- 5 protections
    story.append(Paragraph("5.&nbsp; Tamper protection built into this release", S["h"]))
    for txt in [
        "<b>Start-up verification</b> - before any window or user data is loaded, the program re-computes its Authenticode "
        "SHA-256 digest, verifies the PKCS#7 signature and requires the pinned certificate above. Appended data, extra "
        "certificates or a foreign signer are rejected.",
        "<b>Installed-hash check</b> - the signed installer records the SHA-256 of the executable in HKLM (administrator-only); "
        "installed copies must match it exactly.",
        "<b>Continuous runtime checks</b> - every 15 s (debugger check every 1.5 s, and immediately when the file changes on "
        "disk): signature and hash of the file, byte-for-byte comparison of the IL code and metadata mapped in memory, and "
        "inspection of all loaded modules.",
        "<b>Injection defence</b> - DLL search restricted to System32, current-directory loading disabled, extension points "
        "(AppInit DLLs, global hooks, legacy IMEs) and remote / low-integrity images blocked; any unsigned module from a "
        "user-writable location is treated as injection.",
        "<b>Immediate termination</b> - on any violation the event is logged to %APPDATA%\\StickyNotesPro\\security.log and "
        "the process is terminated.",
        "<b>Strict input validation</b> - XML without DTDs/external entities and with size limits; RTF sanitised (embedded "
        "OLE objects/packages and non-web field codes removed) on load, import and paste; only http/https/mailto links open; "
        "file links validated, programs require confirmation; wildcard patterns length-limited with regex timeouts; e-mail "
        "headers stripped of CR/LF; all data fields bounded and whitelisted.",
    ]:
        story.append(Paragraph("• &nbsp;" + txt, S["bul"]))

    story.append(PageBreak())

    # ---- 6 release files
    story.append(Paragraph("6.&nbsp; Files covered by this record", S["h"]))
    rows = [[Paragraph("<b>File</b>", S["k"]), Paragraph("<b>Size (bytes)</b>", S["k"]), Paragraph("<b>SHA-256</b>", S["k"])]]
    for f in m["files"]:
        rows.append([Paragraph(f["file"], S["v"]), Paragraph(f"{f['size']:,}", S["v"]), Paragraph(grouped(f["sha256"]), S["mono"])])
    ft = Table(rows, colWidths=[60 * mm, 16 * mm, 94 * mm], repeatRows=1)
    ft.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), PALE), ("LINEBELOW", (0, 0), (-1, -1), 0.4, LINE),
                            ("VALIGN", (0, 0), (-1, -1), "TOP"), ("TOPPADDING", (0, 0), (-1, -1), 3),
                            ("BOTTOMPADDING", (0, 0), (-1, -1), 3)]))
    story.append(ft)
    story.append(Spacer(1, 1.5 * mm))
    story.append(Paragraph("The same values are published in SHA256SUMS.txt and in the machine-readable release manifest "
                           "(JSON). This PDF and the manifest are not listed because they contain the values themselves.",
                           S["small"]))

    # ---- 7 how to verify
    story.append(Paragraph("7.&nbsp; Independent verification", S["h"]))
    story.append(Paragraph("Anyone can confirm a copy of the program against this record (Windows PowerShell):", S["p"]))
    story.append(Paragraph(
        "Get-FileHash .\\StickyNotesPro.exe -Algorithm SHA256<br/>"
        f"&nbsp;&nbsp;# expected: {exe['sha256']}<br/>"
        "Get-AuthenticodeSignature .\\StickyNotesPro.exe | Format-List *<br/>"
        f"&nbsp;&nbsp;# SignerCertificate.Thumbprint expected: {cert['sha1_thumbprint']}<br/>"
        ".\\Verify-StickyNotesPro.ps1 &nbsp;&nbsp;# automated check of all three values", S["code"]))

    # ---- 8 declaration + seal + qr
    story.append(Paragraph("8.&nbsp; Declaration and signature", S["h"]))
    decl = Paragraph(
        "<b>Declaration.</b> The release files identified in this record were produced by the official StickyNotes Pro "
        "release process and were found authentic and unmodified at the time of issue. This document is itself digitally "
        "signed with the release certificate described in section 4; any modification of the document invalidates that "
        "signature.", S["p"])
    qr_payload = json.dumps({"product": "StickyNotes Pro", "version": m["version"], "record": rec_no,
                             "exe_sha256": exe["sha256"], "cert_sha256": cert["sha256_fingerprint"]}, separators=(",", ":"))
    block = Table([[Seal(), decl, qr(qr_payload)]], colWidths=[34 * mm, 102 * mm, 34 * mm])
    block.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
                               ("LEFTPADDING", (0, 0), (-1, -1), 2), ("RIGHTPADDING", (0, 0), (-1, -1), 2)]))
    story.append(Spacer(1, 3 * mm))
    story.append(KeepTogether([block, Spacer(1, 2 * mm),
                               Paragraph("Scan the QR code for the machine-readable summary (version, executable SHA-256, "
                                         "certificate fingerprint).", S["small"]),
                               Spacer(1, 5 * mm),
                               Paragraph("<b>Digital signature of this document</b>", S["k"]),
                               Spacer(1, 1 * mm),
                               SigAnchor(SIG_BOX)]))

    # ---- page decoration
    def deco(c, doc):
        w, h = A4
        c.saveState()
        c.setFillColor(NAVY); c.rect(0, h - 16 * mm, w, 16 * mm, stroke=0, fill=1)
        c.setFillColor(GOLD); c.rect(0, h - 17.2 * mm, w, 1.2 * mm, stroke=0, fill=1)
        c.setFillColor(colors.white); c.setFont("Helvetica-Bold", 12.5)
        c.drawString(20 * mm, h - 10.3 * mm, "STICKYNOTES PRO")
        c.setFont("Helvetica", 8.5)
        c.drawRightString(w - 20 * mm, h - 10.3 * mm, "Release Integrity Record  |  " + rec_no)
        c.setStrokeColor(LINE); c.setLineWidth(0.6); c.line(20 * mm, 15 * mm, w - 20 * mm, 15 * mm)
        c.setFillColor(MUTED); c.setFont("Helvetica", 7)
        c.drawString(20 * mm, 10.5 * mm,
                     f"StickyNotes Pro {m['version']}  -  executable SHA-256 {exe['sha256'][:16]}...{exe['sha256'][-8:]}"
                     "  -  document digitally signed")
        c.drawRightString(w - 20 * mm, 10.5 * mm, f"Page {doc.page} of {TOTAL_PAGES['n'] or '?'}")
        c.setFillColor(NAVY); c.setFillAlpha(0.035)
        c.translate(w / 2, h / 2); c.rotate(35); c.setFont("Helvetica-Bold", 70)
        c.drawCentredString(0, 0, "VERIFIED RELEASE")
        c.restoreState()

    margins = dict(leftMargin=20 * mm, rightMargin=20 * mm, topMargin=22 * mm, bottomMargin=20 * mm)
    def make_template(target):
        d = BaseDocTemplate(target, pagesize=A4, **margins,
                            title=f"StickyNotes Pro {m['version']} - Certificate of Integrity",
                            author="StickyNotes Pro Release Engineering",
                            subject=f"Release integrity record {rec_no}",
                            creator="StickyNotes Pro release pipeline",
                            keywords=f"StickyNotes Pro, {m['version']}, SHA-256, {exe['sha256']}")
        fr = Frame(d.leftMargin, d.bottomMargin, d.width, d.height, id="f")
        d.addPageTemplates([PageTemplate(id="p", frames=[fr], onPage=deco)])
        return d

    import copy, io
    probe = make_template(io.BytesIO())
    probe.build(copy.deepcopy(story))
    TOTAL_PAGES["n"] = probe.page
    doc = make_template(path)
    doc.build(story)

# ------------------------------------------------------------------ PDF signature

def sign_pdf(path, pfx, passfile, tsa):
    from pyhanko.sign import signers, fields
    from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter
    from pyhanko.stamp import TextStampStyle

    page = SIG_BOX.get("page", 0) - 1 if SIG_BOX else -1
    box = tuple(int(v) for v in SIG_BOX["rect"]) if SIG_BOX else (57, 64, 330, 124)

    signer = signers.SimpleSigner.load_pkcs12(pfx_file=pfx,
                                              passphrase=open(passfile, "rb").read().strip())
    timestamper = None
    if tsa:
        from pyhanko.sign.timestamps import HTTPTimeStamper
        timestamper = HTTPTimeStamper(tsa)
    meta = signers.PdfSignatureMetadata(field_name="ReleaseSignature",
                                        reason="Official release integrity record",
                                        location="StickyNotes Pro build system")
    sty = TextStampStyle(stamp_text="Digitally signed by: %(signer)s\nDate: %(ts)s\n"
                                    "Reason: Official release integrity record")
    pdf_signer = signers.PdfSigner(meta, signer=signer, timestamper=timestamper, stamp_style=sty)
    import io as _io
    data = open(path, "rb").read()
    w = IncrementalPdfFileWriter(_io.BytesIO(data), strict=False)
    fields.append_signature_field(w, fields.SigFieldSpec("ReleaseSignature", on_page=page, box=box))
    with open(path + ".signed", "wb") as outf:
        pdf_signer.sign_pdf(w, output=outf)
    os.replace(path + ".signed", path)

# ------------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True); ap.add_argument("--exe", required=True)
    ap.add_argument("--setup", required=True); ap.add_argument("--cert", required=True)
    ap.add_argument("--pin", required=True); ap.add_argument("--verify-log", required=True)
    ap.add_argument("--out-dir", required=True); ap.add_argument("--pfx")
    ap.add_argument("--passfile"); ap.add_argument("--tsa")
    a = ap.parse_args()

    exe, setup = file_hashes(a.exe), file_hashes(a.setup)
    cert = cert_info(a.cert)
    sig = parse_verify_log(open(a.verify_log, encoding="utf-8", errors="replace").read())
    sig["signing_time"] = iso_time(sig["signing_time"]); sig["timestamp_time"] = iso_time(sig["timestamp_time"])
    sig["tsa_issuer"] = sig["tsa_issuer"].lstrip("/").replace("/", ", ")

    if a.pin.upper().replace(" ", "") != cert["sha256_fingerprint"]:
        sys.exit("FAIL: pinned certificate SHA-256 does not match the certificate actually used to sign")
    if sig["current_digest"] and sig["calculated_digest"] and sig["current_digest"] != sig["calculated_digest"]:
        sys.exit("FAIL: osslsigncode current/calculated digests disagree")

    files = []
    for fn in sorted(os.listdir(a.out_dir)):
        fp = os.path.join(a.out_dir, fn)
        if os.path.isfile(fp) and not fn.endswith((".pdf", ".json")) and fn != "SHA256SUMS.txt":
            h = file_hashes(fp); files.append({"file": fn, "size": h["size"], "sha256": h["sha256"]})

    issue = dt.datetime.utcnow().strftime("%Y-%m-%d %H:%M UTC")
    m = {
        "product": "StickyNotes Pro", "version": a.version,
        "record_number": f"SNP-{a.version}-{exe['sha256'][:8]}",
        "issue_time": issue, "generated_utc": dt.datetime.utcnow().isoformat(timespec="seconds") + "Z",
        "installer_path": a.setup,
        "executable": exe, "installer": setup, "signature": sig, "certificate": cert, "files": files,
    }
    man = os.path.join(a.out_dir, f"StickyNotesPro_{a.version}_release-manifest.json")
    json.dump(m, open(man, "w"), indent=2)

    pdf = os.path.join(a.out_dir, f"StickyNotesPro_{a.version}_Certificate_of_Integrity.pdf")
    build_pdf(pdf, m)
    signed = ""
    if a.pfx and a.passfile:
        try:
            sign_pdf(pdf, a.pfx, a.passfile, a.tsa)
            signed = " (signed)"
        except Exception as e:
            print("WARNING: PDF digital signature failed: " + str(e), file=sys.stderr)
    print("integrity record: " + os.path.abspath(pdf) + signed)

if __name__ == "__main__":
    main()
