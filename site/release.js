// Fills the page with the latest release, read from GitHub when the page loads: the same source the
// Windows app's updater uses, so a release shows here as soon as it is published. Elements name
// what they show:
//   data-release-text="{windows.file} · {windows.mb} MB"   -> text
//   data-release-href="{windows.url}"                      -> link
// Values: version, tag, published, notes, and for windows and android: file, url, bytes, mb, sha256.
// Until this runs, or if GitHub can't be reached (it answers 60 requests an hour per address
// without a login) or a value is missing, the page keeps its fallbacks: links to the latest
// release on GitHub, which are never wrong.
(() => {
  const LATEST = "https://api.github.com/repos/RizwanNaasir/air-lens/releases/latest";
  const FILES = { windows: /^AirLens-.+-windows-x64\.zip$/, android: /^AirLens-.+-android\.apk$/ };

  // GitHub's own checksum of the uploaded file ("digest": "sha256:<hex>"), or else the
  // "<sha256>  <file>" line the release notes list.
  const sha256 = (asset, notes) => {
    const digest = /^sha256:([0-9a-f]{64})$/i.exec(asset.digest || "");
    if (digest) return digest[1].toLowerCase();
    for (const line of notes.split("\n")) {
      const listed = /^([0-9a-f]{64})\s+\*?(\S+)$/i.exec(line.trim());
      if (listed && listed[2] === asset.name) return listed[1].toLowerCase();
    }
    return undefined;
  };

  const fromGitHub = (latest) => {
    const notes = latest.body || "";
    const file = (pattern) => {
      const asset = (latest.assets || []).find((a) => pattern.test(a.name));
      if (!asset) return undefined;
      return {
        file: asset.name,
        url: asset.browser_download_url,
        bytes: asset.size,
        mb: Math.round(asset.size / 1048576),
        sha256: sha256(asset, notes),
      };
    };
    return {
      version: (latest.tag_name || "").replace(/^v/i, ""),
      tag: latest.tag_name,
      published: (latest.published_at || "").slice(0, 10),
      notes: latest.html_url,
      windows: file(FILES.windows),
      android: file(FILES.android),
    };
  };

  const fillPage = (release) => {
    const value = (path) => path.split(".").reduce((node, key) => (node == null ? undefined : node[key]), release);
    const fill = (template) =>
      template.replace(/\{([\w.]+)\}/g, (_, path) => {
        const v = value(path);
        if (v === undefined || v === null || v === "") throw new Error(`the release has no ${path}`);
        return String(v);
      });
    for (const el of document.querySelectorAll("[data-release-text]")) {
      try { el.textContent = fill(el.dataset.releaseText); } catch { /* keep the fallback */ }
    }
    for (const el of document.querySelectorAll("[data-release-href]")) {
      try { el.href = fill(el.dataset.releaseHref); } catch { /* keep the fallback */ }
    }
  };

  (async () => {
    let latest;
    try {
      const response = await fetch(LATEST);
      if (!response.ok) return;
      latest = await response.json();
    } catch {
      return;
    }
    fillPage(fromGitHub(latest));
  })();
})();
