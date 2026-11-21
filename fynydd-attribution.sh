#!/bin/bash

# Step 1: Rename directories first, from deepest to shallowest.
find . -type d -name '*Argentini*' -print0 | sort -rz | while IFS= read -r -d $'\0' dir; do
    newdir=$(echo "$dir" | sed 's/Argentini/Fynydd/g')
    echo "Renaming directory '$dir' to '$newdir'"
    mv "$dir" "$newdir" || { echo "Failed to rename $dir"; exit 1; }
done

# Step 2: After directories are renamed, rename files.
find . -type f -name '*Argentini*' -print0 | while IFS= read -r -d $'\0' file; do
    newname=$(echo "$file" | sed 's/Argentini/Fynydd/g')
    echo "Renaming file '$file' to '$newname'"
    mv "$file" "$newname" || { echo "Failed to rename $file"; exit 1; }
done

# Step 3: modify contents of every non-hidden, non-binary file under cwd
find . \
  -type d -path '*/.*' -prune \
  -o \
  \( \
    -type f \
    ! -name '.*' \
    ! -name 'fynydd-attribution.sh' \
    -exec grep -Iqi 'Argentini' {} \; \
    -print0 \
  \) \
| while IFS= read -r -d '' file; do
    echo "Modifying '$file'"
    sed -i '' \
      -e 's/Michael Argentini/Fynydd LLC/g' \
      -e 's/Argentini Unlimited, Ltd/Fynydd LLC/g' \
      -e 's/Argentini/Fynydd/g' \
      -e 's/argentini/fynydd/g' \
      "$file" \
    || { echo "Failed to modify $file"; exit 1; }
done
