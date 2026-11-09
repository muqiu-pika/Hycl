#!/usr/bin/env node
"use strict";
// Cross-platform single-file publish verifier for .github/workflows/build.yml.
// Usage: node verify-publish.cjs <dir> <output-name> <minMB> <maxMB>
// Checks that the publish directory contains exactly one file of a sane size,
// then normalizes its name so artifact uploads are identical on every RID.
const fs = require("node:fs");
const path = require("node:path");

const [dir, outName, minMbRaw, maxMbRaw] = process.argv.slice(2);
if (!dir || !outName) {
  console.error("usage: node verify-publish.cjs <dir> <output-name> <minMB> <maxMB>");
  process.exit(2);
}

const min = Number(minMbRaw || 0) * 1048576;
const max = Number(maxMbRaw || 0) > 0 ? Number(maxMbRaw) * 1048576 : Infinity;
const files = fs.readdirSync(dir).filter((name) => fs.statSync(path.join(dir, name)).isFile());
if (files.length !== 1) {
  throw new Error(`expected exactly one file in ${dir}, got ${files.length}: ${files.join(", ")}`);
}

const source = path.join(dir, files[0]);
const size = fs.statSync(source).size;
if (size < min || size > max) {
  throw new Error(
    `unexpected single-file size ${(size / 1048576).toFixed(1)} MB (expected ${minMbRaw}-${maxMbRaw} MB)`
  );
}

const target = path.join(dir, outName);
if (source !== target) {
  fs.renameSync(source, target);
}
console.log(`OK ${outName}: ${(size / 1048576).toFixed(1)} MB`);
