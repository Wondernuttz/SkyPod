cmake_minimum_required(VERSION 3.24)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "" FORCE)

project(SkypodPlugin VERSION 1.0.0 LANGUAGES CXX)

include("${CMAKE_SOURCE_DIR}/LocalPaths.cmake")

if(NOT EXISTS "${CommonLibPath}/vcpkg.json")
    message(FATAL_ERROR "CommonLibSSE-NG was not found at ${CommonLibPath}")
endif()

set(VCPKG_INSTALLED_DIR "${PRISMA_BUILD_ROOT}/build/vcpkg_installed" CACHE STRING "" FORCE)
list(APPEND CMAKE_PREFIX_PATH "${VCPKG_INSTALLED_DIR}/x64-windows-static/share")
set(RAPIDCSV_INCLUDE_DIRS "${VCPKG_INSTALLED_DIR}/x64-windows-static/include" CACHE PATH "" FORCE)

set(ENABLE_SKYRIM_SE OFF CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_AE OFF CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_VR ON CACHE BOOL "" FORCE)
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)

file(READ "${CommonLibPath}/vcpkg.json" _commonlib_json)
string(JSON COMMONLIBSSE_VERSION GET "${_commonlib_json}" "version-semver")
set(COMMONLIBSSE_VERSION "${COMMONLIBSSE_VERSION}" CACHE STRING "" FORCE)

set(_saved_build_type "${CMAKE_BUILD_TYPE}")
set(CMAKE_BUILD_TYPE "Release")
add_compile_definitions(_CRT_SECURE_NO_WARNINGS NDEBUG)
add_subdirectory("${CommonLibPath}" "${CMAKE_BINARY_DIR}/external_builds/CommonLibSSE" EXCLUDE_FROM_ALL)
include("${CommonLibPath}/cmake/CommonLibSSE.cmake")
set(CMAKE_BUILD_TYPE "${_saved_build_type}")

if(MSVC)
    # MSVC otherwise stores absolute __FILE__/source-location paths in the
    # release DLL. Map local roots to stable public labels so distributable
    # packages never expose a developer's Windows account or machine layout.
    file(TO_NATIVE_PATH "${CMAKE_SOURCE_DIR}" _skypod_source_native)
    file(TO_NATIVE_PATH "${CommonLibPath}" _commonlib_source_native)
    file(TO_NATIVE_PATH "${PRISMA_BUILD_ROOT}" _prisma_source_native)
    set(_public_path_map_options
        /experimental:deterministic
        "/pathmap:${_skypod_source_native}=Skypod"
        "/pathmap:${_commonlib_source_native}=CommonLibSSE"
        "/pathmap:${_prisma_source_native}=ThirdParty"
    )
    target_compile_options(CommonLibSSE PRIVATE ${_public_path_map_options})
endif()

add_commonlibsse_plugin(${PROJECT_NAME}
    NAME "Skypod"
    AUTHOR "Wondernuttz"
    VERSION ${PROJECT_VERSION}
    USE_ADDRESS_LIBRARY
    SOURCES
        src/main.cpp
        src/HotkeyManager.cpp
        src/PrismaBridge.cpp
        src/RuntimeMusic.cpp
        src/BossMusic.cpp
        src/Settings.cpp
)

target_include_directories(${PROJECT_NAME} PRIVATE include)
target_precompile_headers(${PROJECT_NAME} PRIVATE include/PCH.h)
target_compile_definitions(${PROJECT_NAME} PRIVATE
    UNICODE
    _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    COMMONLIBSSE_VERSION="${COMMONLIBSSE_VERSION}"
)

if(MSVC)
    target_compile_options(${PROJECT_NAME} PRIVATE
        /W4 /permissive- /Zc:preprocessor /utf-8
        ${_public_path_map_options}
    )
endif()

if(SKYPOD_DEPLOY)
    set(_destination "${CMAKE_SOURCE_DIR}/../../mod/Data/SKSE/Plugins")
    add_custom_command(TARGET ${PROJECT_NAME} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${_destination}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:${PROJECT_NAME}>" "${_destination}/Skypod.dll"
        COMMENT "Deploying Skypod.dll to the local MO2 package"
        VERBATIM)
endif()
