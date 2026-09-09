import CoreGraphics
import Foundation

/// Model extension point only. An annotation editor is intentionally out of scope for phase one.
struct AnnotationModel: Identifiable, Sendable {
    enum Kind: Sendable { case arrow, line, rectangle, circle, highlighter, text, number, blur, mosaic }
    let id: UUID
    let kind: Kind
    let bounds: CGRect
}
