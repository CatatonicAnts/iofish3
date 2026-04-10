if(NOT USE_FREETYPE)
    return()
endif()

if(NOT BUILD_RENDERER_GL1 AND NOT BUILD_RENDERER_GL2)
    return()
endif()

# Build FreeType from submodule source
set(FREETYPE_SOURCE_DIR "${CMAKE_SOURCE_DIR}/code/libs/freetype")

if(EXISTS "${FREETYPE_SOURCE_DIR}/CMakeLists.txt")
    # Disable optional FreeType dependencies we don't need
    set(FT_DISABLE_BZIP2 ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_BROTLI ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_PNG ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_ZLIB ON CACHE BOOL "" FORCE)
    set(SKIP_INSTALL_ALL ON CACHE BOOL "" FORCE)

    add_subdirectory("${FREETYPE_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/freetype" EXCLUDE_FROM_ALL)

    list(APPEND RENDERER_INCLUDE_DIRS "${FREETYPE_SOURCE_DIR}/include")
    list(APPEND RENDERER_LIBRARIES freetype)
else()
    # Fallback to system-installed FreeType
    find_package(Freetype REQUIRED)
    list(APPEND RENDERER_INCLUDE_DIRS ${FREETYPE_INCLUDE_DIRS})
    list(APPEND RENDERER_LIBRARIES ${FREETYPE_LIBRARIES})
endif()
