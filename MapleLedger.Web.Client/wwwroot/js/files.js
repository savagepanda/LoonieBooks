// Saves text produced in the browser as a file, without a round trip to the server.
export function downloadText(fileName, contentType, text) {
    save(fileName, new Blob([text], { type: contentType }));
}

// Blazor passes a .NET byte[] as a Uint8Array.
export function downloadBytes(fileName, contentType, bytes) {
    save(fileName, new Blob([bytes], { type: contentType }));
}

function save(fileName, blob) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
