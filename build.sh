#!/usr/bin/env bash

set -eu
set -o pipefail

cd `dirname $0`

PAKET_BOOTSTRAPPER_EXE=.paket/paket.bootstrapper.exe
PAKET_EXE=.paket/paket.exe
FAKE_EXE=packages/build/FAKE/tools/FAKE.exe
BUILD_FSX=build.fsx

FSIARGS=""
OS=${OS:-"unknown"}
if [[ "$OS" != "Windows_NT" ]]
then
  FSIARGS="--fsiargs -d:MONO"
fi

function run() {
  if [[ "$OS" != "Windows_NT" ]]
  then
    mono "$@"
  else
    "$@"
  fi
}

run $PAKET_BOOTSTRAPPER_EXE

run $PAKET_EXE restore

[ ! -e $BUILD_FSX ] && run $PAKET_EXE update
[ ! -e $BUILD_FSX ] && run $FAKE_EXE init.fsx
run $FAKE_EXE "$@" $FSIARGS $BUILD_FSX

