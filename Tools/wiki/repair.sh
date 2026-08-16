#!/bin/bash
# Повторный прогон категорий, которые в основном проходе испортились.
#
# «мед» — pi вернул ноль байт: восемнадцать минут, пустой stdout, пустой stderr, код 0.
# «химия» — статьи писались по дайджесту, в котором поле `impact` выводилось как «взрыв силы».
#   На деле это LogImpact, важность записи в админском логе (ReactionPrototype.cs:68). Модель
#   добросовестно перенесла выдумку, и у творога появилась взрывчатость. Дайджест исправлен,
#   заодно продукт-предмет теперь виден: «FoodCheese ← Milk 40 + Enzyme 5».
cd /home/rewin/projects/ss14_ai || exit 1
python3 Tools/wiki/digest.py
rm -f wiki_skills/химия-*.md
for cat in мед химия; do
  echo "############ ПОВТОР $cat  $(date +%H:%M) ############"
  python3 Tools/wiki/run.py "$cat" 2>&1 | tail -20
  python3 Tools/wiki/lint.py "$cat" 2>&1 | tail -20
done
echo "############ ПОВТОР ГОТОВ $(date +%H:%M) ############"
