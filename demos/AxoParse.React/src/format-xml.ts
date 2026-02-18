/**
 * Indent an XML string for human-readable display.
 * Uses a simple tag-boundary approach — no full XML parser needed.
 *
 * @param xml - Raw XML string (single line or already partially formatted).
 * @param indent - Indentation string per level (default two spaces).
 * @returns Pretty-printed XML string.
 */
export function formatXml(xml: string, indent: string = "  "): string {
    // Normalise to single line, collapse whitespace between tags
    let s = xml.replace(/>\s+</g, "><").trim()
    const parts: string[] = []
    let depth = 0
    let pos = 0

    while (pos < s.length) {
        const lt = s.indexOf("<", pos)
        if (lt === -1) {
            // Trailing text
            const text = s.slice(pos).trim()
            if (text) parts.push(indent.repeat(depth) + text)
            break
        }

        // Text before the tag
        if (lt > pos) {
            const text = s.slice(pos, lt).trim()
            if (text) parts.push(indent.repeat(depth) + text)
        }

        const gt = s.indexOf(">", lt)
        if (gt === -1) break
        const tag = s.slice(lt, gt + 1)
        pos = gt + 1

        if (tag.startsWith("<?") || tag.startsWith("<!")) {
            // Processing instruction or comment — keep at current depth
            parts.push(indent.repeat(depth) + tag)
        } else if (tag.startsWith("</")) {
            // Closing tag — decrease depth first
            depth--
            parts.push(indent.repeat(Math.max(0, depth)) + tag)
        } else if (tag.endsWith("/>")) {
            // Self-closing tag
            parts.push(indent.repeat(depth) + tag)
        } else {
            // Opening tag — check if next content is a closing tag (inline text element)
            const nextLt = s.indexOf("<", pos)
            if (nextLt !== -1 && s[nextLt + 1] === "/") {
                // Inline: <Tag>text</Tag> — keep on one line
                const closeGt = s.indexOf(">", nextLt)
                if (closeGt !== -1) {
                    const inlineText = s.slice(pos, nextLt)
                    const closeTag = s.slice(nextLt, closeGt + 1)
                    parts.push(indent.repeat(depth) + tag + inlineText + closeTag)
                    pos = closeGt + 1
                    continue
                }
            }
            parts.push(indent.repeat(depth) + tag)
            depth++
        }
    }

    return parts.join("\n")
}
