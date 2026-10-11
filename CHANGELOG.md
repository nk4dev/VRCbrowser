# Changelog

## Unreleased

- Fixed: a Home Url typed on a prefab instance in the scene was not saved.
- `<frameset>` pages are shown as one document. Frames load from the new Frame Urls list, the bookmarks or the Home Url.
- Pages in Shift_JIS and EUC-JP are decoded.
- Presentational attributes of older pages are honoured: `bgcolor`, `align`, `<font color>` and `<body text>`.
- More CSS: `margin` / `padding` on all four sides, `max-width` and centred columns, `line-height`, `text-transform`, `text-indent`, `white-space`, `list-style-type`, the `font` shorthand, `opacity: 0`, `hsl()` and many more named colors, `vw` / `ch` units, custom properties with `var()`, and `@media` by page width.
- More HTML: indented `blockquote`, `dd`, `figure` and nested lists; `ol start` / `type` and `li value`; `q`, `ruby`, `big`, `caption`, `summary`, `dialog`, `font size`, the `hidden` attribute; form controls drawn as text; numeric and many more named character entities.
- Lists are now indented with padding instead of full-width spaces, and padding applies to blocks without a background too.
- Added a custom inspector for `VRCbrowser`: bookmarks are edited as label / URL rows, and the prefab's UI references are folded away.
- The prefab no longer ships with preset bookmarks.
- The default Welcome Page no longer mentions the bookmark menu.

## 0.1.0

- Initial release.
