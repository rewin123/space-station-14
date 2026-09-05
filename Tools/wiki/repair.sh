#!/bin/bash
# Re-run categories that got messed up in the main pass.
#
# "мед" (medical) — pi returned zero bytes: eighteen minutes, empty stdout, empty stderr, exit
#   code 0.
# "химия" (chemistry) — articles were written from the digest, where the `impact` field was
#   rendered as "blast force". In reality it's LogImpact, the importance of an entry in the
#   admin log (ReactionPrototype.cs:68). The model faithfully carried the fabrication over,
#   and cheese ended up explosive. The digest is fixed, and as a bonus the product item is now
#   visible: "FoodCheese ← Milk 40 + Enzyme 5".
cd /home/rewin/projects/ss14_ai || exit 1
python3 Tools/wiki/digest.py
rm -f wiki_skills/химия-*.md
for cat in мед химия; do
  echo "############ ПОВТОР $cat  $(date +%H:%M) ############"
  python3 Tools/wiki/run.py "$cat" 2>&1 | tail -20
  python3 Tools/wiki/lint.py "$cat" 2>&1 | tail -20
done
echo "############ ПОВТОР ГОТОВ $(date +%H:%M) ############"
