"""Builds assets/popup-font.ttf, the popup's font (title and timer), from the Cinzel variable font.

    python make_font.py <path to Cinzel[wght].ttf> [<output .ttf>]

Get Cinzel[wght].ttf from https://github.com/google/fonts/tree/main/ofl/cinzel (SIL Open Font
License 1.1, no Reserved Font Name; the licence is in docs/licenses/Cinzel-OFL.txt).

What happens to it:
- a static instance at weight 700 (GDI+ only uses a variable font's default weight);
- overlapping contours merged: GDI+ fills overlaps as holes under its default fill rule, which
  spoiled the digits 4 and 8 (needs skia-pathops);
- cut down to printable ASCII (U+0020-U+007E), keeping kerning.
"""
import os
import sys
import tempfile

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    out = sys.argv[2] if len(sys.argv) > 2 else 'popup-font.ttf'
    static = instancer.instantiateVariableFont(TTFont(sys.argv[1]), {'wght': 700}, updateFontNames=True,
                                                 overlap=instancer.OverlapMode.REMOVE)
    tmp = os.path.join(tempfile.mkdtemp(), 'static.ttf')
    static.save(tmp)
    options = subset.Options()
    options.layout_features = ['kern']
    font = subset.load_font(tmp, options)
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=range(0x20, 0x7F))
    subsetter.subset(font)
    subset.save_font(font, out, options)
    print('%s: %d glyphs, %d bytes' % (out, len(font.getGlyphOrder()), os.path.getsize(out)))


if __name__ == '__main__':
    main()
