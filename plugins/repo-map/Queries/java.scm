; Source: tree-sitter/tree-sitter-java @ e10607b45ff745f5f876bfa3e94fbcc6b44bdc11, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.* or @reference.*; the rest is ignored.

(class_declaration
  name: (identifier) @name) @definition.class

(method_declaration
  name: (identifier) @name) @definition.method

(method_invocation
  name: (identifier) @name
  arguments: (argument_list) @reference.call)

(interface_declaration
  name: (identifier) @name) @definition.interface

(type_list
  (type_identifier) @name) @reference.implementation

(object_creation_expression
  type: (type_identifier) @name) @reference.class

(superclass (type_identifier) @name) @reference.class

; --- cxagent local additions ------------------------------------------------------------------
; Upstream misses constructors, enums, records and annotation types, and type names outside new and extends.
(constructor_declaration name: (identifier) @name) @definition.method
(enum_declaration name: (identifier) @name) @definition.class
(record_declaration name: (identifier) @name) @definition.class
(annotation_type_declaration name: (identifier) @name) @definition.interface
((type_identifier) @name @reference.type)
(method_invocation object: (identifier) @name) @reference.type
