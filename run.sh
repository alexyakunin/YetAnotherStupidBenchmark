#!/bin/bash

pushd () {
    command pushd "$@" > /dev/null
}

popd () {
    command popd "$@" > /dev/null
}

if [ ! -f "Test.dat" ]; then
    pushd DotNetCore
    ./generate.sh
    popd
fi

pushd DotNetCore
echo .NET Core 3.1:
./run-netcore31.sh
./run-netcore31.sh
./run-netcore31.sh
echo
echo .NET 5.0:
./run.sh
./run.sh
./run.sh
echo
popd

pushd Cpp
echo GCC:
./run.sh
./run.sh
./run.sh
popd
