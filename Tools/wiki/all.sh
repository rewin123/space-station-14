#!/bin/bash
# Run the remaining categories one after another. Sequentially, not in parallel: there's one
# model, one GPU, and two agents hitting it would just queue up anyway, only interleaving and
# ruining the log.
#
# The order follows where the most was lost: station technology first, where the wiki gives
# procedures and dozens of machine names, then rules last, where the library is already detailed.
cd /home/rewin/projects/ss14_ai || exit 1
for cat in питание атмосфера наука мед химия строй снабжение сервис антаг должности виды общее силикон закон правила; do
  echo "############ $cat  $(date +%H:%M) ############"
  python3 Tools/wiki/run.py "$cat" 2>&1 | tail -20
  python3 Tools/wiki/lint.py "$cat" 2>&1 | tail -20
done
echo "############ ГОТОВО $(date +%H:%M) ############"
