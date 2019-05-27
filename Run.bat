@echo off

if not exist "Test.dat" (
  pushd DotNetCore
  call Generate.bat
  popd
)

pushd DotNetCore
call Run.bat
call Run.bat
call Run.bat
call Run.bat
call Run.bat
popd

pushd Cpp
call Run.bat
call Run.bat
call Run.bat
call Run.bat
call Run.bat
popd
