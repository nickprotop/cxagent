; Source: tree-sitter/tree-sitter-rust @ 261b20226c04ef601adbdf185a800512a5f66291, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.* or @reference.*; the rest is ignored.

; ADT definitions

(struct_item
    name: (type_identifier) @name) @definition.class

(enum_item
    name: (type_identifier) @name) @definition.class

(union_item
    name: (type_identifier) @name) @definition.class

; type aliases

(type_item
    name: (type_identifier) @name) @definition.class

; method definitions

(declaration_list
    (function_item
        name: (identifier) @name) @definition.method)

; function definitions

(function_item
    name: (identifier) @name) @definition.function

; trait definitions
(trait_item
    name: (type_identifier) @name) @definition.interface

; module definitions
(mod_item
    name: (identifier) @name) @definition.module

; macro definitions

(macro_definition
    name: (identifier) @name) @definition.macro

; references

(call_expression
    function: (identifier) @name) @reference.call

(call_expression
    function: (field_expression
        field: (field_identifier) @name)) @reference.call

(macro_invocation
    macro: (identifier) @name) @reference.call

; implementations

(impl_item
    trait: (type_identifier) @name) @reference.implementation

(impl_item
    type: (type_identifier) @name
    !trait) @reference.implementation

; --- cxagent local additions ------------------------------------------------------------------
; Upstream marks type names only in impl headers, and an impl block as a reference, not as the
; block of methods a reader looks for; and a trait's methods that have no body yet.
(function_signature_item name: (identifier) @name) @definition.method
(impl_item type: (type_identifier) @name) @definition.class
(impl_item type: (generic_type type: (type_identifier) @name)) @definition.class
((type_identifier) @name @reference.type)
; An impl for a type that is not a plain or generic path — a slice, a primitive, a reference — and
; a trait's associated types.
(impl_item type: [(array_type) (primitive_type) (reference_type) (pointer_type) (tuple_type) (scoped_type_identifier)] @name) @definition.class
(associated_type name: (type_identifier) @name) @definition.type
