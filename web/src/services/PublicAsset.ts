/**
 * Resolves a file in the public directory against the site's base URL.
 * @param path - The file's path from the public directory, such as "/logo.webp".
 * @returns The URL to load the file from.
 * @remarks Vite applies the base URL to public paths in HTML and CSS, but not to
 * strings in scripts or templates. Without this, the site only works when it is
 * served from a domain root, not from a subpath such as a GitHub Pages project site.
 */
export function publicAsset(path: string): string {
  return import.meta.env.BASE_URL + path.replace(/^\//, "");
}
