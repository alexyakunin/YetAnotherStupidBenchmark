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
./run-netcore31.sh
./run-netcore31.sh
./run-netcore31.sh
./run.sh
./run.sh
./run.sh
popd

pushd Cpp
./run.sh
./run.sh
./run.sh
popd
