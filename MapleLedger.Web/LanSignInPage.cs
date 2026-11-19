using System.Net;

namespace MapleLedger.Web;

/// <summary>
/// The passcode page another device sees when it opens a shared desktop install. Plain HTML with
/// no script, like the phone upload page, so it works before the app has loaded.
/// </summary>
internal static class LanSignInPage
{
    public static string Render(string nonce, string returnUrl, string? error, bool hasCertificate, string? fingerprint) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>Enter the passcode · LoonieBooks</title>
        <style nonce="{{nonce}}">
          :root { color-scheme: light dark; --accent: #1b6ec2; }
          body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0; padding: 24px 16px; max-width: 32rem; margin-inline: auto; line-height: 1.45; }
          h1 { font-size: 1.4rem; margin: 0 0 .5rem; }
          h2 { font-size: 1.05rem; margin: 2rem 0 .25rem; }
          .brand { font-size: .85rem; opacity: .7; margin-bottom: 1rem; }
          label { display: block; font-weight: 600; margin: 1rem 0 .35rem; }
          input[type=password] { box-sizing: border-box; width: 100%; padding: 12px; font-size: 1.05rem; border-radius: 8px; border: 1px solid #8a8a8a; }
          button { width: 100%; margin-top: 12px; padding: 14px; border: 0; border-radius: 10px; background: var(--accent); color: #fff; font-size: 1.05rem; font-weight: 600; }
          .error { color: #c62828; font-weight: 600; }
          .note { font-size: .9rem; opacity: .8; }
          code { font-size: .75rem; word-break: break-all; }
          a { color: var(--accent); }
        </style>
        </head>
        <body>
        <div class="brand">LoonieBooks</div>
        <h1>Enter the household passcode</h1>
        <p>This LoonieBooks is shared on your home network. Enter the passcode set in the LoonieBooks window on the computer; this device then stays signed in.</p>
        {{(error is null ? "" : $"<p class=\"error\" role=\"alert\">{WebUtility.HtmlEncode(error)}</p>")}}
        <form method="post" action="/lan/signin">
          <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}">
          <label for="passcode">Passcode</label>
          <input id="passcode" name="passcode" type="password" autocomplete="current-password" required autofocus>
          <button type="submit">Open LoonieBooks</button>
        </form>
        {{(hasCertificate ? $"""
        <h2>Does your browser warn about this address?</h2>
        <p class="note">
          Install LoonieBooks' certificate on this device once: <a href="/lan/certificate">download it</a>, then trust it
          (iPhone and iPad: Settings › General › VPN &amp; Device Management to install, then Settings › General › About ›
          Certificate Trust Settings; Android: Settings › Security › Encryption &amp; credentials › Install a certificate › CA certificate).
          It should match the fingerprint in the LoonieBooks window:
        </p>
        <p class="note"><code>{WebUtility.HtmlEncode(fingerprint)}</code></p>
        """ : "")}}
        </body>
        </html>
        """;
}
