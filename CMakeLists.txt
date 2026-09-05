# ---------------------------------------------------------------------------
# MoveCopyScrap - CMake front-end for the WinUI 3 / C# application.
#
# CMake cannot compile WinUI 3 C# itself (its built-in CSharp language support
# only emits legacy, non-SDK .csproj files, which cannot run the XAML compiler
# or the Windows App SDK targets).  So CMake takes the role of *driver*:
#
#   cmake -S . -B build -G "Visual Studio 17 2022" -A x64   -> configure
#   cmake --build build --config Release                    -> dotnet publish
#   cmake --install build --prefix C:/Apps/MoveCopyScrap      -> xcopy deploy
#
# The .csproj underneath is a completely normal SDK-style project, so it also
# opens and F5-debugs in Visual Studio 2022 without CMake being involved.
#
# Build driver
# ------------
# By default the `dotnet` CLI is used.  It carries its own copy of the .NET SDK,
# so it works even when the only Visual Studio on the machine is "Build Tools"
# (which ships MSBuild but *not* MSBuild\Sdks\Microsoft.NET.Sdk, and therefore
# cannot build SDK-style C# projects at all).  Set MCS_BUILD_DRIVER=msbuild to
# use a full Visual Studio installation's MSBuild.exe instead.
# ---------------------------------------------------------------------------

cmake_minimum_required(VERSION 3.21)

project(MoveCopyScrap
    VERSION 1.0.0
    DESCRIPTION "Fast image and video culling tool for Windows"
    LANGUAGES NONE)

if(NOT CMAKE_HOST_WIN32)
    message(FATAL_ERROR
        "MoveCopyScrap is a Windows (WinUI 3) application and must be configured on Windows.")
endif()

# ---------------------------------------------------------------------------
# Options
# ---------------------------------------------------------------------------

set(MCS_PROJECT_FILE "${CMAKE_CURRENT_SOURCE_DIR}/src/MoveCopyScrap.csproj"
    CACHE FILEPATH "Path to the MoveCopyScrap .csproj")

set(MCS_RUNTIME_IDENTIFIER "win-x64"
    CACHE STRING "RID to publish for (win-x64 or win-arm64)")
set_property(CACHE MCS_RUNTIME_IDENTIFIER PROPERTY STRINGS win-x64 win-arm64)

set(MCS_BUILD_DRIVER "auto"
    CACHE STRING "Which tool builds the project: auto, dotnet, or msbuild")
set_property(CACHE MCS_BUILD_DRIVER PROPERTY STRINGS auto dotnet msbuild)

option(MCS_SELF_CONTAINED
    "Publish the .NET runtime and the Windows App SDK next to the app (xcopy deployable)" ON)

option(MCS_PARALLEL_BUILD "Build in parallel" ON)

# Deliberately flat. A portable Windows app is expected to unzip to one folder with the
# .exe right there; burying it in bin/ next to a share/doc/ tree is a Unix habit that
# just makes people hunt for the executable. Set these if you want the split back.
set(MCS_INSTALL_BINDIR "." CACHE STRING "Install destination for the application, relative to the prefix")
set(MCS_INSTALL_DOCDIR "." CACHE STRING "Install destination for documentation")

# Publish as ONE self-extracting .exe instead of a folder of ~400 files.
#
# Supported by the Windows App SDK for unpackaged, self-contained apps - see
# Microsoft.WindowsAppSDK.SingleFile.targets, which hard-errors unless EnableMsixTooling
# is on, WindowsPackageType is None and IncludeAllContentForSelfExtract is true. This
# project already satisfies all three.
#
# The cost is a slower first launch: everything, native DLLs included, is unpacked to a
# per-version folder under %TEMP% before the app starts.
option(MCS_SINGLE_FILE "Publish as a single self-extracting executable" OFF)

if(MCS_RUNTIME_IDENTIFIER STREQUAL "win-arm64")
    set(MCS_MSBUILD_PLATFORM "ARM64")
else()
    set(MCS_MSBUILD_PLATFORM "x64")
endif()

# Directory.Build.props puts every MSBuild intermediate under <root>/build, and this
# has to agree with it: the purge step below and the clean-publish target both need to
# know where obj\ actually is.
set(MCS_ARTIFACT_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/build")
set(MCS_PUBLISH_ROOT "${CMAKE_BINARY_DIR}/publish")

if(MCS_SELF_CONTAINED)
    set(MCS_SELF_CONTAINED_VALUE "true")
else()
    set(MCS_SELF_CONTAINED_VALUE "false")
endif()

# ---------------------------------------------------------------------------
# Locate the .NET SDK (preferred driver)
# ---------------------------------------------------------------------------

set(MCS_DOTNET_USABLE FALSE)

find_program(MCS_DOTNET_EXECUTABLE
    NAMES dotnet
    HINTS
        "$ENV{ProgramFiles}/dotnet"
        "$ENV{ProgramW6432}/dotnet"
        "C:/Program Files/dotnet"
    DOC "Path to the dotnet CLI")

if(MCS_DOTNET_EXECUTABLE)
    execute_process(
        COMMAND "${MCS_DOTNET_EXECUTABLE}" --list-sdks
        OUTPUT_VARIABLE _mcs_sdks
        ERROR_VARIABLE  _mcs_sdks_err
        RESULT_VARIABLE _mcs_sdks_rc
        OUTPUT_STRIP_TRAILING_WHITESPACE)

    if(_mcs_sdks_rc EQUAL 0 AND _mcs_sdks)
        string(REPLACE "\r" "" _mcs_sdks "${_mcs_sdks}")
        # Accept .NET 8 or newer.
        if(_mcs_sdks MATCHES "(^|\n)(8|9|1[0-9])\\.")
            set(MCS_DOTNET_USABLE TRUE)
        endif()
        string(REPLACE "\n" ", " _mcs_sdks_oneline "${_mcs_sdks}")
        message(STATUS "MoveCopyScrap: .NET SDKs   = ${_mcs_sdks_oneline}")
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

function(_mcs_msbuild_has_net_sdk candidate out_var)
    get_filename_component(_bin "${candidate}" DIRECTORY)          # ...\MSBuild\Current\Bin
    get_filename_component(_cur "${_bin}" DIRECTORY)               # ...\MSBuild\Current
    get_filename_component(_msb "${_cur}" DIRECTORY)               # ...\MSBuild
    if(EXISTS "${_msb}/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props")
        set(${out_var} TRUE PARENT_SCOPE)
    else()
        set(${out_var} FALSE PARENT_SCOPE)
    endif()
endfunction()

find_program(MCS_VSWHERE_EXECUTABLE
    NAMES vswhere.exe
    HINTS
        "$ENV{ProgramFiles\(x86\)}/Microsoft Visual Studio/Installer"
        "$ENV{ProgramFiles}/Microsoft Visual Studio/Installer"
        "C:/Program Files (x86)/Microsoft Visual Studio/Installer"
    DOC "Path to vswhere.exe (ships with the Visual Studio Installer)")

if(NOT MCS_MSBUILD_EXECUTABLE AND MCS_VSWHERE_EXECUTABLE)
    execute_process(
        COMMAND "${MCS_VSWHERE_EXECUTABLE}"
                -all
                -prerelease
                -products "*"
                -version "[17.0,18.0)"
                -requires Microsoft.Component.MSBuild
                -find "MSBuild/**/Bin/MSBuild.exe"
        OUTPUT_VARIABLE _mcs_vswhere_out
        RESULT_VARIABLE _mcs_vswhere_rc
        OUTPUT_STRIP_TRAILING_WHITESPACE)

    if(_mcs_vswhere_rc EQUAL 0 AND _mcs_vswhere_out)
        string(REPLACE "\r" "" _mcs_vswhere_out "${_mcs_vswhere_out}")
        string(REPLACE "\n" ";" _mcs_msbuild_list "${_mcs_vswhere_out}")

        foreach(_candidate IN LISTS _mcs_msbuild_list)
            if(NOT _candidate)
                continue()
            endif()
            file(TO_CMAKE_PATH "${_candidate}" _candidate)
            _mcs_msbuild_has_net_sdk("${_candidate}" _has_sdk)
            if(_has_sdk)
                set(MCS_MSBUILD_EXECUTABLE "${_candidate}"
                    CACHE FILEPATH "Path to an MSBuild.exe that can build SDK-style C# projects" FORCE)
                break()
            else()
                message(STATUS "MoveCopyScrap: skipping ${_candidate} (no Microsoft.NET.Sdk)")
            endif()
        endforeach()
    endif()
endif()

# ---------------------------------------------------------------------------
# Choose the driver
# ---------------------------------------------------------------------------

if(MCS_BUILD_DRIVER STREQUAL "dotnet")
    if(NOT MCS_DOTNET_USABLE)
        message(FATAL_ERROR
            "MCS_BUILD_DRIVER=dotnet but no .NET 8+ SDK was found.\n"
            "Install it from https://dotnet.microsoft.com/download/dotnet/8.0 "
            "(the 'SDK', not just the runtime), or pass -DMCS_BUILD_DRIVER=msbuild.")
    endif()
    set(_mcs_driver "dotnet")
elseif(MCS_BUILD_DRIVER STREQUAL "msbuild")
    if(NOT MCS_MSBUILD_EXECUTABLE)
        message(FATAL_ERROR
            "MCS_BUILD_DRIVER=msbuild but no MSBuild.exe capable of building SDK-style C# "
            "projects was found.\nInstall the '.NET desktop development' workload in the "
            "Visual Studio 2022 Installer, or pass -DMCS_MSBUILD_EXECUTABLE=<path>.")
    endif()
    set(_mcs_driver "msbuild")
elseif(MCS_DOTNET_USABLE)
    set(_mcs_driver "dotnet")
elseif(MCS_MSBUILD_EXECUTABLE)
    set(_mcs_driver "msbuild")
else()
    set(_mcs_hint "")
    if(MCS_DOTNET_EXECUTABLE)
        set(_mcs_hint
            "  'dotnet' was found at ${MCS_DOTNET_EXECUTABLE} but reports no .NET 8+ SDK.\n"
            "  You most likely have only the .NET *runtime* installed; the SDK is a separate download.\n")
    endif()

    message(FATAL_ERROR
        "No usable build driver found - nothing on this machine can compile C#.\n"
        ${_mcs_hint}
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

message(STATUS "MoveCopyScrap: driver      = ${_mcs_driver}")
if(_mcs_driver STREQUAL "dotnet")
    message(STATUS "MoveCopyScrap: dotnet      = ${MCS_DOTNET_EXECUTABLE}")
else()
    message(STATUS "MoveCopyScrap: MSBuild     = ${MCS_MSBUILD_EXECUTABLE}")
endif()
message(STATUS "MoveCopyScrap: RID         = ${MCS_RUNTIME_IDENTIFIER} (Platform=${MCS_MSBUILD_PLATFORM})")
message(STATUS "MoveCopyScrap: self-contained = ${MCS_SELF_CONTAINED_VALUE}")
message(STATUS "MoveCopyScrap: single file    = ${MCS_SINGLE_FILE}")
message(STATUS "MoveCopyScrap: publish root   = ${MCS_PUBLISH_ROOT}")

# ---------------------------------------------------------------------------
# Catch mistyped -D options.
#
# CMake accepts any -D<name>=<value> and records it whether or not the project
# ever reads it, so a typo like -DCS_SINGLE_FILE=ON configures perfectly
# cleanly and silently does nothing. CMake does report unused variables, but
# only at the very end of a long configure log where it is easy to miss.
#
# A cache entry still typed UNINITIALIZED at this point is one that came from
# the command line and was never declared by this project.
# ---------------------------------------------------------------------------

get_cmake_property(_mcs_cache_vars CACHE_VARIABLES)
set(_mcs_unused "")
foreach(_mcs_var IN LISTS _mcs_cache_vars)
    get_property(_mcs_var_type CACHE "${_mcs_var}" PROPERTY TYPE)
    if(_mcs_var_type STREQUAL "UNINITIALIZED")
        list(APPEND _mcs_unused "${_mcs_var}")
    endif()
endforeach()

if(_mcs_unused)
    string(REPLACE ";" "\n    " _mcs_unused_text "${_mcs_unused}")
    message(WARNING
        "Set on the command line but not used by MoveCopyScrap:\n"
        "    ${_mcs_unused_text}\n"
        "Check the spelling -- every option this project defines starts with MCS_:\n"
        "    MCS_SINGLE_FILE  MCS_SELF_CONTAINED  MCS_RUNTIME_IDENTIFIER  MCS_BUILD_DRIVER\n"
        "    MCS_PARALLEL_BUILD  MCS_INSTALL_BINDIR  MCS_INSTALL_DOCDIR")
endif()

# ---------------------------------------------------------------------------
# Build
#
# $<CONFIG> makes this work with both single- and multi-config generators.
# CMake's RelWithDebInfo / MinSizeRel are mapped onto "Release".
# ---------------------------------------------------------------------------

set(MCS_CONFIG "$<IF:$<CONFIG:Debug>,Debug,Release>")
set(MCS_PUBLISH_DIR "${MCS_PUBLISH_ROOT}/${MCS_CONFIG}")

set(MCS_COMMON_PROPS
    "-p:Platform=${MCS_MSBUILD_PLATFORM}"
    "-p:WindowsAppSDKSelfContained=${MCS_SELF_CONTAINED_VALUE}"
    "-p:WindowsPackageType=None"
    "-p:Version=${PROJECT_VERSION}"
    "-p:FileVersion=${PROJECT_VERSION}.0"
    "-p:InformationalVersion=${PROJECT_VERSION}")

if(MCS_SINGLE_FILE)
    list(APPEND MCS_COMMON_PROPS
        "-p:PublishSingleFile=true"
        "-p:IncludeAllContentForSelfExtract=true"
        # Without this the Windows App SDK warns that
        # MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY must be set before program entry.
        # App.xaml.cs sets it from a [ModuleInitializer] as well, belt and braces.
        "-p:WindowsAppSdkUndockedRegFreeWinRTInitialize=true")
endif()

if(_mcs_driver STREQUAL "dotnet")
    # NOTE: `dotnet publish` does not accept MSBuild's bare -m switch, so parallelism
    # is requested through the property instead.
    if(MCS_PARALLEL_BUILD)
        set(_mcs_parallel "-p:BuildInParallel=true")
    else()
        set(_mcs_parallel "")
    endif()

    # `dotnet publish` restores implicitly, so the assets file always matches the
    # properties the publish is done with - no restore/build property drift.
    add_custom_target(MoveCopyScrap ALL
        COMMAND ${CMAKE_COMMAND} "-DSRC_DIR=${CMAKE_CURRENT_SOURCE_DIR}/src"
                "-DROOT_DIR=${CMAKE_CURRENT_SOURCE_DIR}"
                "-DOBJ_ROOT=${MCS_ARTIFACT_ROOT}/obj"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/PurgeStaleAppHost.cmake"
        COMMAND ${CMAKE_COMMAND} -E echo "== dotnet publish MoveCopyScrap (${MCS_CONFIG}, ${MCS_RUNTIME_IDENTIFIER}) =="
        COMMAND "${MCS_DOTNET_EXECUTABLE}" publish "${MCS_PROJECT_FILE}"
                --nologo
                -c "${MCS_CONFIG}"
                -r "${MCS_RUNTIME_IDENTIFIER}"
                "--self-contained" "${MCS_SELF_CONTAINED_VALUE}"
                -o "${MCS_PUBLISH_DIR}"
                -v minimal
                ${_mcs_parallel}
                ${MCS_COMMON_PROPS}
        BYPRODUCTS "${MCS_PUBLISH_DIR}/MoveCopyScrap.exe"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Building MoveCopyScrap with the .NET SDK"
        VERBATIM
        USES_TERMINAL)
else()
    if(MCS_PARALLEL_BUILD)
        set(_mcs_parallel "-m")
    else()
        set(_mcs_parallel "")
    endif()

    add_custom_target(MoveCopyScrap ALL
        COMMAND ${CMAKE_COMMAND} "-DSRC_DIR=${CMAKE_CURRENT_SOURCE_DIR}/src"
                "-DROOT_DIR=${CMAKE_CURRENT_SOURCE_DIR}"
                "-DOBJ_ROOT=${MCS_ARTIFACT_ROOT}/obj"
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/PurgeStaleAppHost.cmake"
        COMMAND ${CMAKE_COMMAND} -E echo "== Restoring NuGet packages =="
        COMMAND "${MCS_MSBUILD_EXECUTABLE}" "${MCS_PROJECT_FILE}"
                -nologo -v:minimal -t:Restore
                "-p:Configuration=${MCS_CONFIG}"
                "-p:RuntimeIdentifier=${MCS_RUNTIME_IDENTIFIER}"
                "-p:SelfContained=${MCS_SELF_CONTAINED_VALUE}"
                ${MCS_COMMON_PROPS}
        COMMAND ${CMAKE_COMMAND} -E echo "== Publishing MoveCopyScrap (${MCS_CONFIG}) =="
        COMMAND "${MCS_MSBUILD_EXECUTABLE}" "${MCS_PROJECT_FILE}"
                -nologo -v:minimal ${_mcs_parallel} -t:Publish
                "-p:Configuration=${MCS_CONFIG}"
                "-p:RuntimeIdentifier=${MCS_RUNTIME_IDENTIFIER}"
                "-p:SelfContained=${MCS_SELF_CONTAINED_VALUE}"
                ${MCS_COMMON_PROPS}
                "-p:PublishDir=${MCS_PUBLISH_DIR}/"
        BYPRODUCTS "${MCS_PUBLISH_DIR}/MoveCopyScrap.exe"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Building MoveCopyScrap with MSBuild"
        VERBATIM
        USES_TERMINAL)
endif()

# Convenience: cmake --build build --target run
add_custom_target(run
    COMMAND "${MCS_PUBLISH_DIR}/MoveCopyScrap.exe"
    DEPENDS MoveCopyScrap
    COMMENT "Running MoveCopyScrap"
    VERBATIM
    USES_TERMINAL)

# Convenience: wipe build output without deleting the CMake cache.
add_custom_target(clean-publish
    COMMAND ${CMAKE_COMMAND} -E rm -rf "${MCS_PUBLISH_ROOT}" "${MCS_ARTIFACT_ROOT}/obj" "${MCS_ARTIFACT_ROOT}/bin"
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

if(MCS_INSTALL_BINDIR STREQUAL "." OR MCS_INSTALL_BINDIR STREQUAL "")
    set(_mcs_bin_dest "\${CMAKE_INSTALL_PREFIX}")
else()
    set(_mcs_bin_dest "\${CMAKE_INSTALL_PREFIX}/${MCS_INSTALL_BINDIR}")
endif()

install(CODE "
set(_mcs_src \"${MCS_PUBLISH_DIR}\")
if(NOT EXISTS \"\${_mcs_src}/MoveCopyScrap.exe\")
    message(FATAL_ERROR
        \"Nothing to install: \${_mcs_src}/MoveCopyScrap.exe does not exist.\\n\"
        \"Run 'cmake --build <builddir> --config <cfg>' first, and install the same config.\")
endif()
message(STATUS \"Installing MoveCopyScrap from \${_mcs_src}\")
file(INSTALL
     DESTINATION \"${_mcs_bin_dest}\"
     TYPE DIRECTORY
     FILES \"\${_mcs_src}/\"
     USE_SOURCE_PERMISSIONS)
")

install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/README.md"
        DESTINATION "${MCS_INSTALL_DOCDIR}"
        OPTIONAL)

# ---------------------------------------------------------------------------
# Packaging (cpack -G ZIP produces an xcopy-deployable archive)
# ---------------------------------------------------------------------------

set(CPACK_PACKAGE_NAME "MoveCopyScrap")
set(CPACK_PACKAGE_VENDOR "MoveCopyScrap")
set(CPACK_PACKAGE_VERSION "${PROJECT_VERSION}")
set(CPACK_PACKAGE_FILE_NAME "MoveCopyScrap-${PROJECT_VERSION}-${MCS_RUNTIME_IDENTIFIER}")
set(CPACK_GENERATOR "ZIP")
include(CPack)
