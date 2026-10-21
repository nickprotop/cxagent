; Source: tree-sitter/tree-sitter-typescript @ 75b3874edb2dc714fb1fd77a32013d0f8699989f, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.* or @reference.*; the rest is ignored.

(function_signature
  name: (identifier) @name) @definition.function

(method_signature
  name: (property_identifier) @name) @definition.method

(abstract_method_signature
  name: (property_identifier) @name) @definition.method

(abstract_class_declaration
  name: (type_identifier) @name) @definition.class

(module
  name: (identifier) @name) @definition.module

(interface_declaration
  name: (type_identifier) @name) @definition.interface

(type_annotation
  (type_identifier) @name) @reference.type

(new_expression
  constructor: (identifier) @name) @reference.class

; --- cxagent local additions ------------------------------------------------------------------
; Upstream builds on JavaScript's and still leaves out type aliases, enums and most type uses.
(type_alias_declaration name: (type_identifier) @name) @definition.type
(enum_declaration name: (identifier) @name) @definition.class
((type_identifier) @name @reference.type)
(extends_clause value: (identifier) @name) @reference.class
