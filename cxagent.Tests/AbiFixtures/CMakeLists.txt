# THE NATIVE TEST FIXTURES, ON EVERY PLATFORM. build.sh makes the same set with cc on Linux and macOS;
# this is the build that also runs on Windows, and the one CI uses everywhere. KEEP THE TWO LISTS
# IDENTICAL: a variant added to one and not the other is a fixture some platforms never test.
cmake_minimum_required(VERSION 3.20)
project(cxagent_abi_fixtures C)

# THE REPORTING FIXTURE STARTS A THREAD OF ITS OWN, as a real plugin reporting late would.
find_package(Threads REQUIRED)

set(FIXTURE_OUT "${CMAKE_BINARY_DIR}" CACHE PATH "Where the fixture libraries go")

# NAME AND THE -D FLAG THAT SELECTS ITS MISBEHAVIOUR, pairwise; NONE for the well-formed one.
set(FIXTURE_NAMES
    fixture-wellformed fixture-malformed fixture-crash fixture-badversion fixture-disagrees
    fixture-noinvoke fixture-submits fixture-commands fixture-reports)
set(FIXTURE_FLAGS
    NONE FIXTURE_MALFORMED FIXTURE_CRASH FIXTURE_BADVERSION FIXTURE_DISAGREES
    FIXTURE_NOINVOKE FIXTURE_SUBMITS FIXTURE_COMMANDS FIXTURE_REPORTS)

foreach(name flag IN ZIP_LISTS FIXTURE_NAMES FIXTURE_FLAGS)
    add_library(${name} SHARED fixture_plugin.c)
    target_include_directories(${name} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../cxagent.Core/sdk/c)
    if(NOT flag STREQUAL "NONE")
        target_compile_definitions(${name} PRIVATE ${flag})
    endif()
    target_link_libraries(${name} PRIVATE Threads::Threads)
    # HIDDEN BY DEFAULT, so CXAGENT_PLUGIN_EXPORT is what decides the export table on Linux and macOS
    # just as it does under MSVC: an export missing the macro fails these tests everywhere. NO "lib"
    # PREFIX and every configuration into one folder, so the tests find fixture-wellformed.so /
    # .dylib / .dll by one name.
    set_target_properties(${name} PROPERTIES
        PREFIX ""
        C_VISIBILITY_PRESET hidden
        LIBRARY_OUTPUT_DIRECTORY ${FIXTURE_OUT}
        RUNTIME_OUTPUT_DIRECTORY ${FIXTURE_OUT}
        LIBRARY_OUTPUT_DIRECTORY_DEBUG ${FIXTURE_OUT}
        RUNTIME_OUTPUT_DIRECTORY_DEBUG ${FIXTURE_OUT}
        LIBRARY_OUTPUT_DIRECTORY_RELEASE ${FIXTURE_OUT}
        RUNTIME_OUTPUT_DIRECTORY_RELEASE ${FIXTURE_OUT})
endforeach()

# THE C SDK'S FIXTURE: written with cxp, built by cxp_add_plugin as an author's plugin would be — so
# the helper itself is under test, not only the code it compiles. Counting allocations lets a test
# see that every string handed to the host is freed.
include(${CMAKE_CURRENT_SOURCE_DIR}/../../cxagent.Core/sdk/c/cxp.cmake)
cxp_add_plugin(cxp-fixture SIDECAR ${CMAKE_CURRENT_SOURCE_DIR}/cxp/cxp-fixture.plugin.json SOURCES cxp/cxp_fixture.c)
target_compile_definitions(cxp-fixture PRIVATE CXP_COUNT_ALLOCATIONS)
set_target_properties(cxp-fixture PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${FIXTURE_OUT}
    RUNTIME_OUTPUT_DIRECTORY ${FIXTURE_OUT}
    LIBRARY_OUTPUT_DIRECTORY_DEBUG ${FIXTURE_OUT}
    RUNTIME_OUTPUT_DIRECTORY_DEBUG ${FIXTURE_OUT}
    LIBRARY_OUTPUT_DIRECTORY_RELEASE ${FIXTURE_OUT}
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${FIXTURE_OUT})
