// Fills the page with the current release from release.json, which the release workflow
// (.github/workflows/release.yml) rewrites after every release. Elements name what they show:
//   data-release-text="{windows.file} · {windows.mb} MB"   -> text
//   data-release-href="{windows.url}"                      -> link
// Until this runs, or if a value is missing, the page keeps its fallbacks: links to the latest
// release on GitHub, which are never wrong.
(async () => {
  let release;
  try {
    const response = await fetch("release.json", { cache: "no-cache" });
    if (!response.ok) return;
    release = await response.json();
  } catch {
    return;
  }
  const value = (path) => path.split(".").reduce((node, key) => (node == null ? undefined : node[key]), release);
  const fill = (template) =>
    template.replace(/\{([\w.]+)\}/g, (_, path) => {
      const v = value(path);
      if (v === undefined || v === null || v === "") throw new Error(`release.json has no ${path}`);
      return String(v);
    });
  for (const el of document.querySelectorAll("[data-release-text]")) {
    try { el.textContent = fill(el.dataset.releaseText); } catch { /* keep the fallback */ }
  }
  for (const el of document.querySelectorAll("[data-release-href]")) {
    try { el.href = fill(el.dataset.releaseHref); } catch { /* keep the fallback */ }
  }
})();
