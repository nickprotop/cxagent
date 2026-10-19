# cxp.cmake — build a cxagent plugin with the C SDK.
#
#   include(cxp/cxp.cmake)
#   cxp_add_plugin(<target> SIDECAR <name>.plugin.json SOURCES <your .c files>)
#
# Needs CMake 3.20 (string(JSON ...)).
#
# A shared library implementing the cxagent plugin ABI, built so the sidecar is the one truth:
#   - the sidecar is embedded as describe's answer;
#   - its "pluginContract" is the handshake;
#   - poll is compiled in only with "client": true, and command only when "commands" lists any —
#     cxagent polls any library that exports poll, so exporting it unasked costs every plugin a timer;
#   - the library is named after the sidecar and the sidecar copied beside it, because cxagent finds
#     one from the other.
#
# VENDORED: cJSON 1.7.18 (MIT, see cJSON-LICENSE), unmodified, from github.com/DaveGamble/cJSON tag v1.7.18.
#   cJSON.c  sha256 75c51de8fa40ac9d7a99319c6330719bd692eb81c0a869265f3d4c682533f9b9
#   cJSON.h  sha256 0578cc29132912edbc88f83207a8fc76e5db3db0605497e909a9384ef3cc474b

cmake_minimum_required(VERSION 3.20)
set(CXP_DIR "${CMAKE_CURRENT_LIST_DIR}")

function(cxp_add_plugin target)
  cmake_parse_arguments(CXP "" "SIDECAR" "SOURCES" ${ARGN})
  if(NOT CXP_SIDECAR)
    message(FATAL_ERROR "cxp_add_plugin(${target}): SIDECAR <name>.plugin.json is required.")
  endif()
  get_filename_component(sidecar "${CXP_SIDECAR}" ABSOLUTE)
  if(NOT EXISTS "${sidecar}")
    message(FATAL_ERROR "cxp_add_plugin(${target}): no sidecar at ${sidecar}.")
  endif()

  # A SIDECAR THIS CANNOT READ FAILS THE CONFIGURE, naming the file: a library built from it would
  # describe nothing cxagent can load. Types too — a string contract would reach the compiler as
  # garbage, and "client": "yes" would export poll for a plugin that never submits.
  file(READ "${sidecar}" json)
  string(JSON contract_type ERROR_VARIABLE err TYPE "${json}" pluginContract)
  if(err OR NOT contract_type STREQUAL "NUMBER")
    message(FATAL_ERROR "${sidecar}: \"pluginContract\" must be a number (${err}${contract_type}).")
  endif()
  string(JSON contract GET "${json}" pluginContract)
  string(JSON client_type ERROR_VARIABLE noclient TYPE "${json}" client)
  if(NOT noclient AND NOT client_type STREQUAL "BOOLEAN")
    message(FATAL_ERROR "${sidecar}: \"client\" must be true or false, not ${client_type}.")
  endif()
  if(NOT noclient)
    string(JSON client GET "${json}" client)
  endif()
  string(JSON commands ERROR_VARIABLE nocommands LENGTH "${json}" commands)

  # THE SIDECAR AS BYTES, not a string literal: no escaping to get wrong, no trigraph a "??=" in a
  # description would trip, no source code page, and no limit on a literal's length (MSVC refuses one
  # past 16 KB). Written only when it changed, so an unchanged sidecar rebuilds nothing.
  file(READ "${sidecar}" hex HEX)
  string(REGEX REPLACE "([0-9a-f][0-9a-f])" "0x\\1," bytes "${hex}")
  set(generated "${CMAKE_CURRENT_BINARY_DIR}/${target}-cxp")
  configure_file("${CXP_DIR}/cxp_manifest.h.in" "${generated}/cxp_manifest.h" @ONLY)

  add_library(${target} SHARED ${CXP_SOURCES} "${CXP_DIR}/cxp.c" "${CXP_DIR}/cJSON.c")
  target_include_directories(${target} PRIVATE "${CXP_DIR}" "${generated}")
  # CJSON_HIDE_SYMBOLS: cJSON is cxp's own; without it a Windows DLL would export all ~80 cJSON_
  # functions beside the plugin's (Linux and macOS hide them through the visibility preset).
  target_compile_definitions(${target} PRIVATE CXP_CONTRACT=${contract} CJSON_HIDE_SYMBOLS)
  if(NOT noclient AND client)
    target_compile_definitions(${target} PRIVATE CXP_CLIENT)
  endif()
  if(NOT nocommands AND commands GREATER 0)
    target_compile_definitions(${target} PRIVATE CXP_COMMANDS)
  endif()

  # NAMED AFTER THE SIDECAR, no "lib" prefix: weather.plugin.json beside weather.so, as the loader pairs them.
  get_filename_component(stem "${sidecar}" NAME)
  string(REGEX REPLACE "\\.plugin\\.json$" "" stem "${stem}")
  # "$<1:dir>" AS THE OUTPUT FOLDER: a generator expression stops multi-config generators (Visual
  # Studio, Ninja Multi-Config) adding build/Debug/, so every generator leaves the plugin in the build
  # folder the printed commands and `cxagent plugin check build` look in.
  set_target_properties(${target} PROPERTIES
    OUTPUT_NAME "${stem}"
    PREFIX ""
    C_STANDARD 11
    C_VISIBILITY_PRESET hidden
    LIBRARY_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>"
    RUNTIME_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>")
  find_package(Threads REQUIRED)
  target_link_libraries(${target} PRIVATE Threads::Threads)

  add_custom_command(TARGET ${target} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${sidecar}" "$<TARGET_FILE_DIR:${target}>/${stem}.plugin.json"
    VERBATIM)
  set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${sidecar}")
endfunction()
