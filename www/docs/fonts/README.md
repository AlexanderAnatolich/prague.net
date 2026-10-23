# Fonts

`Excalifont-Regular-latin.woff2` is the Latin subset (U+0020–U+00FF and common punctuation)
of **Excalifont**, the hand-drawn typeface of [Excalidraw](https://excalidraw.com), taken
unchanged from the `@excalidraw/excalidraw` npm package (`dist/prod/fonts/Excalifont/`).
Excalidraw publishes Excalifont under the SIL Open Font License 1.1.

It is not loaded by any page. `scripts/embed-svg-fonts.py` inlines it into the exported
diagram SVGs in `images/` at build time so the diagrams render with the same handwriting in
every browser; without it `<img>`-loaded SVGs cannot fetch fonts and fall back to a system
cursive face.
