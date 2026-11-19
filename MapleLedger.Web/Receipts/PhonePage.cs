namespace MapleLedger.Web.Receipts;

/// <summary>
/// The small page a phone opens from the QR code: take or choose a photo and send it. Plain HTML
/// so it loads instantly on a phone; the script and styles run only with the response's nonce.
/// </summary>
internal static class PhonePage
{
    public static string Upload(string nonce) => Page(nonce, "Send a photo", """
        <h1>Send a photo</h1>
        <p>Take a photo of the receipt or statement page (flat, well lit, filling the frame) or choose one. It goes straight to LoonieBooks on your computer.</p>
        <label class="button primary">Take a photo<input id="camera" type="file" accept="image/*" capture="environment"></label>
        <label class="button">Choose a photo or PDF<input id="pick" type="file" accept="image/*,application/pdf"></label>
        <img id="preview" alt="">
        <p id="status" role="status"></p>
        """, Script);

    public static string Closed(string nonce) => Page(nonce, "Link expired", """
        <h1>This link has expired</h1>
        <p>Each QR code works once, for 10 minutes. On your computer, choose <b>Send from phone</b> again and scan the new code.</p>
        """, "");

    private static string Page(string nonce, string title, string body, string script) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{title}} · LoonieBooks</title>
        <style nonce="{{nonce}}">
          :root { color-scheme: light dark; --accent: #1b6ec2; }
          body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0; padding: 24px 16px; max-width: 32rem; margin-inline: auto; line-height: 1.45; }
          h1 { font-size: 1.4rem; margin: 0 0 .5rem; }
          .brand { font-size: .85rem; opacity: .7; margin-bottom: 1rem; }
          .button { display: block; text-align: center; padding: 14px; margin: 12px 0; border-radius: 10px; border: 2px solid var(--accent); color: var(--accent); font-size: 1.05rem; font-weight: 600; }
          .button.primary { background: var(--accent); color: #fff; }
          .button.busy { opacity: .5; pointer-events: none; }
          input[type=file] { display: none; }
          #preview { display: none; width: 100%; max-height: 45vh; object-fit: contain; margin-top: 12px; border-radius: 8px; }
          #status { font-weight: 600; }
          .ok { color: #1a7f37; } .error { color: #c62828; }
        </style>
        </head>
        <body>
        <div class="brand">LoonieBooks</div>
        {{body}}
        {{(script.Length > 0 ? $"<script nonce=\"{nonce}\">{script}</script>" : "")}}
        </body>
        </html>
        """;

    // Shrinks photos on the phone before sending (a 12 MP photo becomes a few hundred KB), which
    // keeps the upload quick on Wi-Fi. Formats the browser can't decode are sent as they are.
    private const string Script = """
        const status = document.getElementById('status');
        const preview = document.getElementById('preview');
        const buttons = document.querySelectorAll('.button');
        const say = (text, cls) => { status.textContent = text; status.className = cls || ''; };
        const busy = on => buttons.forEach(b => b.classList.toggle('busy', on));

        async function shrink(file) {
          if (!file.type.startsWith('image/')) return file;
          try {
            const bitmap = await createImageBitmap(file);
            const scale = Math.min(1, 2400 / Math.max(bitmap.width, bitmap.height));
            const canvas = document.createElement('canvas');
            canvas.width = Math.round(bitmap.width * scale);
            canvas.height = Math.round(bitmap.height * scale);
            canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
            bitmap.close();
            const blob = await new Promise(r => canvas.toBlob(r, 'image/jpeg', 0.85));
            return new File([blob], file.name.replace(/\.[^.]*$/, '') + '.jpg', { type: 'image/jpeg' });
          } catch {
            return file;
          }
        }

        async function send(input) {
          const original = input.files[0];
          input.value = '';
          if (!original) return;
          busy(true);
          say('Preparing…');
          try {
            const file = await shrink(original);
            if (file.type.startsWith('image/')) { preview.src = URL.createObjectURL(file); preview.style.display = 'block'; }
            if (file.size > 5 * 1024 * 1024) throw new Error('That file is larger than 5 MB.');
            say('Sending…');
            const response = await fetch(location.pathname, {
              method: 'POST',
              headers: { 'Content-Type': file.type || 'application/octet-stream', 'X-File-Name': encodeURIComponent(file.name || 'receipt') },
              body: file,
            });
            if (!response.ok) {
              let message = 'Sending failed (' + response.status + ').';
              try { const problem = await response.json(); message = problem.detail || problem.title || message; } catch { }
              throw new Error(message);
            }
            say('Sent! Check your computer.', 'ok');
            buttons.forEach(b => b.remove());
          } catch (e) {
            say(e.message || 'Sending failed. Check you are on the same Wi-Fi as your computer.', 'error');
            busy(false);
          }
        }

        document.getElementById('camera').addEventListener('change', e => send(e.target));
        document.getElementById('pick').addEventListener('change', e => send(e.target));
        """;
}
