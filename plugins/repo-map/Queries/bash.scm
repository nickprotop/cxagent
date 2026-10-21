; Source: cxagent. tree-sitter/tree-sitter-bash @ a06c2e4415e9bc0346c6b86d401879ffb44058f7 ships no tags.scm (MIT grammar).

(function_definition name: (word) @name) @definition.function
(command name: (command_name (word) @name)) @reference.call
