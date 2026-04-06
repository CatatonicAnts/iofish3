# Distributed under the OSI-approved BSD 3-Clause License.  See accompanying
# file LICENSE.rst or https://cmake.org/licensing for details.

cmake_minimum_required(VERSION ${CMAKE_VERSION}) # this file comes with cmake

# If CMAKE_DISABLE_SOURCE_CHANGES is set to true and the source directory is an
# existing directory in our source tree, calling file(MAKE_DIRECTORY) on it
# would cause a fatal error, even though it would be a no-op.
if(NOT EXISTS "E:/Projects/iofish3/cmake/tools")
  file(MAKE_DIRECTORY "E:/Projects/iofish3/cmake/tools")
endif()
file(MAKE_DIRECTORY
  "E:/Projects/iofish3/msvc/tools"
  "E:/Projects/iofish3/msvc/qvm_tools-prefix"
  "E:/Projects/iofish3/msvc/qvm_tools-prefix/tmp"
  "E:/Projects/iofish3/msvc/qvm_tools-prefix/src/qvm_tools-stamp"
  "E:/Projects/iofish3/msvc/qvm_tools-prefix/src"
  "E:/Projects/iofish3/msvc/qvm_tools-prefix/src/qvm_tools-stamp"
)

set(configSubDirs Debug;Release;MinSizeRel;RelWithDebInfo)
foreach(subDir IN LISTS configSubDirs)
    file(MAKE_DIRECTORY "E:/Projects/iofish3/msvc/qvm_tools-prefix/src/qvm_tools-stamp/${subDir}")
endforeach()
if(cfgdir)
  file(MAKE_DIRECTORY "E:/Projects/iofish3/msvc/qvm_tools-prefix/src/qvm_tools-stamp${cfgdir}") # cfgdir has leading slash
endif()
