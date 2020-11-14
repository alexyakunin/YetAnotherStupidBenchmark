@echo off

if not exist "Test.dat" (
  pushd DotNetCore
  call Generate.bat
  popd
)

pushd DotNetCore
echo .NET Core 3.1:
call Run-netcore31.bat
call Run-netcore31.bat
call Run-netcore31.bat
echo .

echo .NET 5.0:
call Run.bat
call Run.bat
call Run.bat
echo .
popd

pushd Cpp
echo GCC:
call Run.bat
call Run.bat
call Run.bat
popd
