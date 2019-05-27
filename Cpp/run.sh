#!/bin/bash

pushd cmake-build-release
make
./Cpp "$@"
popd
