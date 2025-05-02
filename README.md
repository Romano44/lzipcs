```Lzipcs is a translation of lzip to C# NET.
Mainly as a curiosity driven benchmark experiment of NET and C#. Original author 
is neither affiliated nor responsible for this project in any way. All the
credit for original lzip goes to Antonio Diaz Diaz. Do NOT contact original
author of lzip if you have issues with lzipcs!

Note that translation from lzip is not 1:1 in everything. Several differences
already exist, such as with arguments, missing fast encoder(-0 | fast),
quiet/verbose mode, altered realtime output during (de)compression, not
deleting original file automatically after operation and more. Future changes
(if any) may widen gap even more, to the point of incompatibility. This is not
meant to be regularly updated 1:1 translation, but a one time fork with own
purpose.

Original code: lzip v1.25,
Copyright (C) {program_year} Antonio Diaz Diaz. 
Original lzip home page: http://www.nongnu.org/lzip/lzip.html


Usage: lzipcs [options] [files]
Options:
  -h, --help                     display this help and exit
  -V, --version                  output version information and exit
  -a, --trailing-error           exit with error status if trailing data
  -b, --member-size=<bytes>      set member size limit of multimember files
  -c, --stdout                   write to standard output, keep input files
  -d, --decompress               decompress, test compressed file integrity
  -f, --force                    overwrite existing output files
  -l, --list                     print (un)compressed file sizes
  -m, --match-length=<bytes>     set match length limit in bytes [36]
  -o, --output=<file>            write to <file>, keep input files
  -s, --dictionary-size=<bytes>  set dictionary size limit in bytes [8 MiB]
  -S, --volume-size=<bytes>      set volume size limit in bytes
  -t, --test                     test compressed file integrity
  -1 .. -9                       set compression level [default 6]
      --best                     alias for -9
      --loose-trailing           allow trailing data seeming corrupt header

NOTE: Short options need space between argument and cannot be stacked together!
```
