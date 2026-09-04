# ---------------------------------------------------------------------------
# ImageCuller2 - CMake front-end for the WinUI 3 / C# application.
#
# CMake cannot compile WinUI 3 C# itself (its built-in CSharp language support
# only emits legacy, non-SDK .csproj files, which cannot run the XAML compiler
# or the Windows App SDK targets).  So CMake takes the role of *driver*:
#
#   cmake -S . -B build -G "Visual Studio 17 2022" -A x64   -> configure
#   cmake --build build --config Release                    -> dotnet publish
#   cmake --install build --prefix C:/Apps/ImageCuller2      -> xcopy deploy
#
# The .csproj underneath is a completely normal SDK-style project, so it also
# opens and F5-debugs in Visual Studio 2022 without CMake being involved.
#
# Build driver
# ------------
# By default the `dotnet` CLI is used.  It carries its own copy of the .NET SDK,
# so it works even when the only Visual Studio on the machine is "Build Tools"
# (which ships MSBuild but *not* MSBuild\Sdks\Microsoft.NET.Sdk, and therefore
# cannot build SDK-style C# projects at all).  Set IC_BUILD_DRIVER=msbuild to
# use a full Visual Studio installation's MSBuild.exe instead.
# ---------------------------------------------------------------------------

cmake_minimum_required(VERSION 3.21)

project(ImageCuller2
    VERSION 1.0.0
    DESCRIPTION "Fast image and video culling tool for Windows"
    LANGUAGES NONE)

if(NOT CMAKE_HOST_WIN32)
    message(FATAL_ERROR
        "ImageCuller2 is a Windows (WinUI 3) application and must be configured on Windows.")
endif()

# ---------------------------------------------------------------------------
# Options
# ---------------------------------------------------------------------------

set(IC_PROJECT_FILE "${CMAKE_CURRENT_SOURCE_DIR}/src/ImageCuller2.csproj"
    CACHE FILEPATH "Path to the ImageCuller2 .csproj")

set(IC_RUNTIME_IDENTIFIER "win-x64"
    CACHE STRING "RID to publish for (win-x64 or win-arm64)")
set_property(CACHE IC_RUNTIME_IDENTIFIER PROPERTY STRINGS win-x64 win-arm64)

set(IC_BUILD_DRIVER "auto"
    CACHE STRING "Which tool builds the project: auto, dotnet, or msbuild")
set_property(CACHE IC_BUILD_DRIVER PROPERTY STRINGS auto dotnet msbuild)

option(IC_SELF_CONTAINED
    "Publish the .NET runtime and the Windows App SDK next to the app (xcopy deployable)" ON)

option(IC_PARALLEL_BUILD "Build in parallel" ON)

set(IC_INSTALL_BINDIR "bin" CACHE STRING "Install destination for the application, relative to the prefix")
set(IC_INSTALL_DOCDIR "share/doc/ImageCuller2" CACHE STRING "Install destination for documentation")

if(IC_RUNTIME_IDENTIFIER STREQUAL "win-arm64")
    set(IC_MSBUILD_PLATFORM "ARM64")
else()
    set(IC_MSBUILD_PLATFORM "x64")
endif()

set(IC_PUBLISH_ROOT "${CMAKE_BINARY_DIR}/publish")

if(IC_SELF_CONTAINED)
    set(IC_SELF_CONTAINED_VALUE "true")
else()
    set(IC_SELF_CONTAINED_VALUE "false")
endif()

# ---------------------------------------------------------------------------
# Locate the .NET SDK (preferred driver)
# ---------------------------------------------------------------------------

set(IC_DOTNET_USABLE FALSE)

find_program(IC_DOTNET_EXECUTABLE
    NAMES dotnet
    HINTS
        "$ENV{ProgramFiles}/dotnet"
        "$ENV{ProgramW6432}/dotnet"
        "C:/Program Files/dotnet"
    DOC "Path to the dotnet CLI")

if(IC_DOTNET_EXECUTABLE)
    execute_process(
        COMMAND "${IC_DOTNET_EXECUTABLE}" --list-sdks
        OUTPUT_VARIABLE _ic_sdks
        ERROR_VARIABLE  _ic_sdks_err
        RESULT_VARIABLE _ic_sdks_rc
        OUTPUT_STRIP_TRAILING_WHITESPACE)

    if(_ic_sdks_rc EQUAL 0 AND _ic_sdks)
        string(REPLACE "\r" "" _ic_sdks "${_ic_sdks}")
        # Accept .NET 8 or newer.
        if(_ic_sdks MATCHES "(^|\n)(8|9|1[0-9])\\.")
            set(IC_DOTNET_USABLE TRUE)
        endif()
        string(REPLACE "\n" ", " _ic_sdks_oneline "${_ic_sdks}")
        message(STATUS "ImageCuller2: .NET SDKs   = ${_ic_sdks_oneline}")
    endif()
endif()

# ---------------------------------------------------------------------------
# Locate a *usable* MSBuild (fallback driver)
#
# Careful: VS 2022 "Build Tools" installs ship MSBuild.exe but do not include
# MSBuild\Sdks\Microsoft.NET.Sdk unless the managed workloads are installed, and
# such an MSBuild fails with MSB4236 on any SDK-style C# project.  So every
# candidate is validated before being accepted.
# ---------------------------------------------------------------------------

function(_ic_msbuild_has_net_sdk candidate out_var)
    get_filename_component(_bin "${candidate}" DIRECTORY)          # ...\MSBuild\Current\Bin
    get_filename_component(_cur "${_bin}" DIRECTORY)               # ...\MSBuild\Current
    get_filename_component(_msb "${_cur}" DIRECTORY)               # ...\MSBuild
    if(EXISTS "${_msb}/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props")
        set(${out_var} TRUE PARENT_SCOPE)
    else()
        set(${out_var} FALSE PARENT_SCOPE)
    endif()
endfunction()

find_program(IC_VSWHERE_EXECUTABLE
    NAMES vswhere.exe
    HINTS
        "$ENV{ProgramFiles\(x86\)}/Microsoft Visual Studio/Installer"
        "$ENV{ProgramFiles}/Microsoft Visual Studio/Installer"
        "C:/Program Files (x86)/Microsoft Visual Studio/Installer"
    DOC "Path to vswhere.exe (ships with the Visual Studio Installer)")

if(NOT IC_MSBUILD_EXECUTABLE AND IC_VSWHERE_EXECUTABLE)
    execute_process(
        COMMAND "${IC_VSWHERE_EXECUTABLE}"
                -all
                -prerelease
                -products "*"
                -version "[17.0,18.0)"
                -requires Microsoft.Component.MSBuild
                -find "MSBuild/**/Bin/MSBuild.exe"
        OUTPUT_VARIABLE _ic_vswhere_out
        RESULT_VARIABLE _ic_vswhere_rc
        OUTPUT_STRIP_TRAILING_WHITESPACE)

    if(_ic_vswhere_rc EQUAL 0 AND _ic_vswhere_out)
        string(REPLACE "\r" "" _ic_vswhere_out "${_ic_vswhere_out}")
        string(REPLACE "\n" ";" _ic_msbuild_list "${_ic_vswhere_out}")

        foreach(_candidate IN LISTS _ic_msbuild_list)
            if(NOT _candidate)
                continue()
            endif()
            file(TO_CMAKE_PATH "${_candidate}" _candidate)
            _ic_msbuild_has_net_sdk("${_candidate}" _has_sdk)
            if(_has_sdk)
                set(IC_MSBUILD_EXECUTABLE "${_candidate}"
                    CACHE FILEPATH "Path to an MSBuild.exe that can build SDK-style C# projects" FORCE)
                break()
            else()
                message(STATUS "ImageCuller2: skipping ${_candidate} (no Microsoft.NET.Sdk)")
            endif()
        endforeach()
    endif()
endif()

# ---------------------------------------------------------------------------
# Choose the driver
# ---------------------------------------------------------------------------

if(IC_BUILD_DRIVER STREQUAL "dotnet")
    if(NOT IC_DOTNET_USABLE)
        message(FATAL_ERROR
            "IC_BUILD_DRIVER=dotnet but no .NET 8+ SDK was found.\n"
            "Install it from https://dotnet.microsoft.com/download/dotnet/8.0 "
            "(the 'SDK', not just the runtime), or pass -DIC_BUILD_DRIVER=msbuild.")
    endif()
    set(_ic_driver "dotnet")
elseif(IC_BUILD_DRIVER STREQUAL "msbuild")
    if(NOT IC_MSBUILD_EXECUTABLE)
        message(FATAL_ERROR
            "IC_BUILD_DRIVER=msbuild but no MSBuild.exe capable of building SDK-style C# "
            "projects was found.\nInstall the '.NET desktop development' workload in the "
            "Visual Studio 2022 Installer, or pass -DIC_MSBUILD_EXECUTABLE=<path>.")
    endif()
    set(_ic_driver "msbuild")
elseif(IC_DOTNET_USABLE)
    set(_ic_driver "dotnet")
elseif(IC_MSBUILD_EXECUTABLE)
    set(_ic_driver "msbuild")
else()
    set(_ic_hint "")
    if(IC_DOTNET_EXECUTABLE)
        set(_ic_hint
            "  'dotnet' was found at ${IC_DOTNET_EXECUTABLE} but reports no .NET 8+ SDK.\n"
            "  You most likely have only the .NET *runtime* installed; the SDK is a separate download.\n")
    endif()

    message(FATAL_ERROR
        "No usable build driver found - nothing on this machine can compile C#.\n"
        ${_ic_hint}
        "Fix it with either of these, then delete the build directory and re-configure:\n"
        "\n"
        "  1. Install the standalone .NET SDK (recommended, smallest download):\n"
        "         winget install --id Microsoft.DotNet.SDK.8 -e\n"
        "     ...then open a NEW terminal so PATH is refreshed.\n"
        "     Manual download: https://dotnet.microsoft.com/download/dotnet/8.0\n"
        "\n"
        "  2. Or add the managed workload to your Visual Studio 2022 installation:\n"
        "         vs_installer.exe modify --installPath \"<your VS install>\" \\\n"
        "             --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools \\\n"
        "             --add Microsoft.NetCore.Component.SDK --passive\n"
        "\n"
        "A Visual Studio 'Build Tools' installation without the managed workloads is not "
        "enough on its own: it ships MSBuild.exe but no MSBuild\\Sdks\\Microsoft.NET.Sdk, "
        "so SDK-style C# projects fail with MSB4236.")
endif()

message(STATUS "ImageCuller2: driver      = ${_ic_driver}")
if(_ic_driver STREQUAL "dotnet")
    message(STATUS "ImageCuller2: dotnet      = ${IC_DOTNET_EXECUTABLE}")
else()
    message(STATUS "ImageCuller2: MSBuild     = ${IC_MSBUILD_EXECUTABLE}")
endif()
message(STATUS "ImageCuller2: RID         = ${IC_RUNTIME_IDENTIFIER} (Platform=${IC_MSBUILD_PLATFORM})")
message(STATUS "ImageCuller2: self-contained = ${IC_SELF_CONTAINED_VALUE}")
message(STATUS "ImageCuller2: publish root   = ${IC_PUBLISH_ROOT}")

# ---------------------------------------------------------------------------
# Build
#
# $<CONFIG> makes this work with both single- and multi-config generators.
# CMake's RelWithDebInfo / MinSizeRel are mapped onto "Release".
# ---------------------------------------------------------------------------

set(IC_CONFIG "$<IF:$<CONFIG:Debug>,Debug,Release>")
set(IC_PUBLISH_DIR "${IC_PUBLISH_ROOT}/${IC_CONFIG}")

set(IC_COMMON_PROPS
    "-p:Platform=${IC_MSBUILD_PLATFORM}"
    "-p:WindowsAppSDKSelfContained=${IC_SELF_CONTAINED_VALUE}"
    "-p:WindowsPackageType=None"
    "-p:Version=${PROJECT_VERSION}"
    "-p:FileVersion=${PROJECT_VERSION}.0"
    "-p:InformationalVersion=${PROJECT_VERSION}")

if(_ic_driver STREQUAL "dotnet")
    # NOTE: `dotnet publish` does not accept MSBuild's bare -m switch, so parallelism
    # is requested through the property instead.
    if(IC_PARALLEL_BUILD)
        set(_ic_parallel "-p:BuildInParallel=true")
    else()
        set(_ic_parallel "")
    endif()

    # `dotnet publish` restores implicitly, so the assets file always matches the
    # properties the publish is done with - no restore/build property drift.
    add_custom_target(ImageCuller2 ALL
        COMMAND ${CMAKE_COMMAND} "-DSRC_DIR=${CMAKE_CURRENT_SOURCE_DIR}/src"
                "-DROOT_DIR=${CMAKE_CURRENT_SOURCE_DIR}"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/PurgeStaleAppHost.cmake"
        COMMAND ${CMAKE_COMMAND} -E echo "== dotnet publish ImageCuller2 (${IC_CONFIG}, ${IC_RUNTIME_IDENTIFIER}) =="
        COMMAND "${IC_DOTNET_EXECUTABLE}" publish "${IC_PROJECT_FILE}"
                --nologo
                -c "${IC_CONFIG}"
                -r "${IC_RUNTIME_IDENTIFIER}"
                "--self-contained" "${IC_SELF_CONTAINED_VALUE}"
                -o "${IC_PUBLISH_DIR}"
                -v minimal
                ${_ic_parallel}
                ${IC_COMMON_PROPS}
        BYPRODUCTS "${IC_PUBLISH_DIR}/ImageCuller2.exe"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Building ImageCuller2 with the .NET SDK"
        VERBATIM
        USES_TERMINAL)
else()
    if(IC_PARALLEL_BUILD)
        set(_ic_parallel "-m")
    else()
        set(_ic_parallel "")
    endif()

    add_custom_target(ImageCuller2 ALL
        COMMAND ${CMAKE_COMMAND} "-DSRC_DIR=${CMAKE_CURRENT_SOURCE_DIR}/src"
                "-DROOT_DIR=${CMAKE_CURRENT_SOURCE_DIR}"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/PurgeStaleAppHost.cmake"
        COMMAND ${CMAKE_COMMAND} -E echo "== Restoring NuGet packages =="
        COMMAND "${IC_MSBUILD_EXECUTABLE}" "${IC_PROJECT_FILE}"
                -nologo -v:minimal -t:Restore
                "-p:Configuration=${IC_CONFIG}"
                "-p:RuntimeIdentifier=${IC_RUNTIME_IDENTIFIER}"
                "-p:SelfContained=${IC_SELF_CONTAINED_VALUE}"
                ${IC_COMMON_PROPS}
        COMMAND ${CMAKE_COMMAND} -E echo "== Publishing ImageCuller2 (${IC_CONFIG}) =="
        COMMAND "${IC_MSBUILD_EXECUTABLE}" "${IC_PROJECT_FILE}"
                -nologo -v:minimal ${_ic_parallel} -t:Publish
                "-p:Configuration=${IC_CONFIG}"
                "-p:RuntimeIdentifier=${IC_RUNTIME_IDENTIFIER}"
                "-p:SelfContained=${IC_SELF_CONTAINED_VALUE}"
                ${IC_COMMON_PROPS}
                "-p:PublishDir=${IC_PUBLISH_DIR}/"
        BYPRODUCTS "${IC_PUBLISH_DIR}/ImageCuller2.exe"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Building ImageCuller2 with MSBuild"
        VERBATIM
        USES_TERMINAL)
endif()

# Convenience: cmake --build build --target run
add_custom_target(run
    COMMAND "${IC_PUBLISH_DIR}/ImageCuller2.exe"
    DEPENDS ImageCuller2
    COMMENT "Running ImageCuller2"
    VERBATIM
    USES_TERMINAL)

# Convenience: wipe build output without deleting the CMake cache.
add_custom_target(clean-publish
    COMMAND ${CMAKE_COMMAND} -E rm -rf "${IC_PUBLISH_ROOT}" "${CMAKE_CURRENT_SOURCE_DIR}/src/obj" "${CMAKE_CURRENT_SOURCE_DIR}/src/bin"
    COMMENT "Removing intermediate and publish output"
    VERBATIM)

# ---------------------------------------------------------------------------
# Install
#
# install(CODE ...) is used (rather than install(DIRECTORY ...)) because the
# source directory contains a $<CONFIG> generator expression, and because
# CMAKE_INSTALL_PREFIX must be resolved at *install* time so that
# `cmake --install build --prefix ...` works as expected.
# ---------------------------------------------------------------------------

install(CODE "
set(_ic_src \"${IC_PUBLISH_DIR}\")
if(NOT EXISTS \"\${_ic_src}/ImageCuller2.exe\")
    message(FATAL_ERROR
        \"Nothing to install: \${_ic_src}/ImageCuller2.exe does not exist.\\n\"
        \"Run 'cmake --build <builddir> --config <cfg>' first, and install the same config.\")
endif()
message(STATUS \"Installing ImageCuller2 from \${_ic_src}\")
file(INSTALL
     DESTINATION \"\${CMAKE_INSTALL_PREFIX}/${IC_INSTALL_BINDIR}\"
     TYPE DIRECTORY
     FILES \"\${_ic_src}/\"
     USE_SOURCE_PERMISSIONS)
")

install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/README.md"
        DESTINATION "${IC_INSTALL_DOCDIR}"
        OPTIONAL)

# ---------------------------------------------------------------------------
# Packaging (cpack -G ZIP produces an xcopy-deployable archive)
# ---------------------------------------------------------------------------

set(CPACK_PACKAGE_NAME "ImageCuller2")
set(CPACK_PACKAGE_VENDOR "ImageCuller2")
set(CPACK_PACKAGE_VERSION "${PROJECT_VERSION}")
set(CPACK_PACKAGE_FILE_NAME "ImageCuller2-${PROJECT_VERSION}-${IC_RUNTIME_IDENTIFIER}")
set(CPACK_GENERATOR "ZIP")
include(CPack)
