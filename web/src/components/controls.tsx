import {
  useEffect, useId, useLayoutEffect, useRef, useState,
  type ButtonHTMLAttributes, type KeyboardEvent, type ReactNode,
} from 'react';
import { IconCheck, IconChevron, IconDash } from './icons';

/* Компоненты управления панели: кнопка, выпадающий список, галочка, переключатель, сегменты, карточки выбора. */

type ButtonVariant = 'primary' | 'secondary' | 'dark' | 'ghost' | 'danger';
type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: 'md' | 'sm';
  /** Действие выполняется: кнопка держит ширину, показывает индикатор и не принимает повторный клик */
  loading?: boolean;
  icon?: ReactNode;
  testId?: string;
};

export function Button({ variant = 'secondary', size = 'md', loading, icon, testId, className, children, onClick, type = 'button', ...rest }: ButtonProps) {
  const cls = ['btn', `btn-${variant}`, size === 'sm' ? 'btn-sm' : '', className ?? ''].filter(Boolean).join(' ');
  return (
    <button {...rest} type={type} className={cls} data-testid={testId} aria-busy={loading || undefined}
      onClick={(e) => {
        if (loading) {
          e.preventDefault();
          return;
        }
        onClick?.(e);
      }}>
      <span className="btn-label">
        {icon && <span className="btn-icon" aria-hidden="true">{icon}</span>}
        {children}
      </span>
      {loading && <span className="btn-spinner" aria-hidden="true" />}
    </button>
  );
}

export type SelectOption<T extends string = string> = { value: T; label: string; description?: string; disabled?: boolean };

type SelectProps<T extends string> = {
  value: T | null;
  options: readonly SelectOption<T>[];
  onChange: (value: T) => void;
  placeholder?: string;
  disabled?: boolean;
  invalid?: boolean;
  /** Подпись для экранного диктора, если рядом нет видимой метки */
  ariaLabel?: string;
  /** id видимой метки */
  labelledBy?: string;
  id?: string;
  testId?: string;
  width?: number | string;
};

const TYPEAHEAD_RESET_MS = 600;

/**
 * Выпадающий список (паттерн «combobox только для выбора»): кнопка-триггер и список вариантов.
 * Клавиатура: ↓/↑/Enter/Space открывают; в списке ↓/↑ — соседний доступный вариант, Home/End — крайние, PageUp/PageDown — через 10,
 * буквы — поиск по началу подписи, Enter/Space — выбрать и закрыть, Esc — закрыть без изменений, Tab — закрыть и уйти дальше.
 */
export function Select<T extends string>({ value, options, onChange, placeholder = 'Выберите…', disabled, invalid, ariaLabel, labelledBy, id, testId, width }: SelectProps<T>) {
  const auto = useId();
  const triggerId = id ?? `${auto}-trigger`;
  const listId = `${auto}-list`;
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const [placement, setPlacement] = useState<'bottom' | 'top'>('bottom');
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const typed = useRef({ text: '', at: 0 });

  const selectedIndex = options.findIndex((o) => o.value === value);
  const selected = selectedIndex >= 0 ? options[selectedIndex] : undefined;
  const enabled = (i: number) => i >= 0 && i < options.length && !options[i]!.disabled;
  /** Ближайший доступный вариант от target в сторону dir; если там нет — в обратную */
  const nearest = (target: number, dir: 1 | -1) => {
    const t = Math.min(options.length - 1, Math.max(0, target));
    for (let i = t; i >= 0 && i < options.length; i += dir) if (enabled(i)) return i;
    for (let i = t; i >= 0 && i < options.length; i -= dir) if (enabled(i)) return i;
    return -1;
  };
  const step = (from: number, dir: 1 | -1) => {
    for (let i = from + dir; i >= 0 && i < options.length; i += dir) if (enabled(i)) return i;
    return from;
  };

  const openList = (at?: number) => {
    if (disabled) return;
    setActive(at ?? (enabled(selectedIndex) ? selectedIndex : nearest(0, 1)));
    setOpen(true);
  };
  const close = (focusTrigger: boolean) => {
    setOpen(false);
    if (focusTrigger) trigger.current?.focus();
  };
  const choose = (i: number) => {
    if (!enabled(i)) return;
    if (options[i]!.value !== value) onChange(options[i]!.value);
    close(true);
  };

  // Закрыть по клику вне списка
  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', onDown);
    return () => document.removeEventListener('pointerdown', onDown);
  }, [open]);

  // Список открывается вверх, если снизу не хватает места
  useLayoutEffect(() => {
    if (!open || !trigger.current) return;
    const r = trigger.current.getBoundingClientRect();
    const below = window.innerHeight - r.bottom;
    setPlacement(below < 300 && r.top > below ? 'top' : 'bottom');
  }, [open]);

  // Активный вариант всегда виден
  useEffect(() => {
    if (!open || active < 0) return;
    list.current?.querySelector<HTMLElement>(`[data-index="${active}"]`)?.scrollIntoView?.({ block: 'nearest' });
  }, [open, active]);

  const typeahead = (ch: string) => {
    const now = Date.now();
    const t = typed.current;
    t.text = now - t.at > TYPEAHEAD_RESET_MS ? ch : t.text + ch;
    t.at = now;
    const query = t.text.toLocaleLowerCase('ru');
    const start = open ? active : selectedIndex;
    // Одна буква (или её повтор) — следующий вариант на эту букву по кругу; несколько разных — поиск с текущего
    const single = [...query].every((c) => c === query[0]);
    const needle = single ? query[0]! : query;
    const from = start < 0 ? 0 : start + (single ? 1 : 0);
    const order = [...options.keys()].map((k) => (from + k) % options.length);
    const hit = order.find((i) => enabled(i) && options[i]!.label.toLocaleLowerCase('ru').startsWith(needle));
    if (hit === undefined) return;
    if (open) setActive(hit);
    else openList(hit);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) return;
    const k = e.key;
    if (!open) {
      if (k === 'ArrowDown' || k === 'ArrowUp' || k === 'Enter' || k === ' ') {
        e.preventDefault();
        openList();
      } else if (k === 'Home' || k === 'End') {
        e.preventDefault();
        openList(k === 'Home' ? nearest(0, 1) : nearest(options.length - 1, -1));
      } else if (k.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
        typeahead(k);
      }
      return;
    }
    switch (k) {
      case 'ArrowDown': e.preventDefault(); setActive((a) => step(a, 1)); break;
      case 'ArrowUp':
        e.preventDefault();
        if (e.altKey) choose(active);
        else setActive((a) => step(a, -1));
        break;
      case 'PageDown': e.preventDefault(); setActive((a) => nearest(a + 10, 1)); break;
      case 'PageUp': e.preventDefault(); setActive((a) => nearest(a - 10, -1)); break;
      case 'Home': e.preventDefault(); setActive(nearest(0, 1)); break;
      case 'End': e.preventDefault(); setActive(nearest(options.length - 1, -1)); break;
      case 'Enter':
      case ' ':
        e.preventDefault();
        choose(active);
        break;
      case 'Escape': e.preventDefault(); close(true); break;
      case 'Tab': close(false); break;
      default:
        if (k.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) typeahead(k);
    }
  };

  return (
    <div className="select" ref={root} style={width !== undefined ? { width } : undefined}>
      <button ref={trigger} id={triggerId} type="button" role="combobox" className="select-trigger" data-testid={testId}
        aria-haspopup="listbox" aria-expanded={open} aria-controls={listId} aria-label={ariaLabel} aria-labelledby={labelledBy ? `${labelledBy} ${triggerId}` : undefined}
        aria-activedescendant={open && active >= 0 ? `${listId}-${active}` : undefined} aria-disabled={disabled || undefined} aria-invalid={invalid || undefined}
        data-value={value ?? ''}
        onClick={() => (open ? close(true) : openList())} onKeyDown={onKeyDown}>
        <span className={`select-value${selected ? '' : ' select-placeholder'}`}>{selected?.label ?? placeholder}</span>
        <IconChevron className="select-chevron" />
      </button>
      {open && (
        <ul ref={list} id={listId} role="listbox" className="select-list" data-placement={placement} aria-label={ariaLabel} aria-labelledby={labelledBy}
          tabIndex={-1} data-testid={testId ? `${testId}-list` : undefined}>
          {options.length === 0 && <li className="select-empty">Нет вариантов</li>}
          {options.map((o, i) => (
            <li key={o.value} id={`${listId}-${i}`} role="option" className="select-option" data-index={i} data-active={i === active || undefined}
              aria-selected={o.value === value} aria-disabled={o.disabled || undefined} data-testid={testId ? `${testId}-option-${o.value}` : undefined}
              onPointerMove={() => enabled(i) && i !== active && setActive(i)}
              onPointerDown={(e) => e.preventDefault()}
              onClick={() => choose(i)}>
              <span className="select-check">{o.value === value && <IconCheck />}</span>
              <span className="select-option-text">
                <span>{o.label}</span>
                {o.description && <span className="select-option-desc">{o.description}</span>}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

type CheckboxProps = {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: ReactNode;
  description?: ReactNode;
  /** Выбрана часть вложенных вариантов; клик выбирает все */
  indeterminate?: boolean;
  disabled?: boolean;
  invalid?: boolean;
  testId?: string;
};

/** Галочка: нативный input (клавиатура, формы, диктор) под фирменным квадратом. Space переключает, клик по подписи — тоже. */
export function Checkbox({ checked, onChange, label, description, indeterminate, disabled, invalid, testId }: CheckboxProps) {
  const ref = useRef<HTMLInputElement>(null);
  const descId = useId();
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = !!indeterminate && !checked;
  }, [indeterminate, checked]);
  return (
    <label className="check" data-disabled={disabled || undefined}>
      <input ref={ref} type="checkbox" className="check-input" checked={checked} disabled={disabled} aria-invalid={invalid || undefined}
        aria-describedby={description ? descId : undefined} data-testid={testId}
        aria-checked={indeterminate && !checked ? 'mixed' : undefined}
        onChange={(e) => onChange(e.target.checked)} />
      <span className="check-box" aria-hidden="true">
        {indeterminate && !checked ? <IconDash className="check-dash" /> : <IconCheck className="check-tick" />}
      </span>
      <span className="check-text">
        <span>{label}</span>
        {description && <span className="check-desc" id={descId}>{description}</span>}
      </span>
    </label>
  );
}

/** Переключатель: действие применяется сразу (без кнопки «Сохранить»). Для настроек формы — Checkbox. */
export function Switch({ checked, onChange, label, description, disabled, testId }: Omit<CheckboxProps, 'indeterminate' | 'invalid'>) {
  const descId = useId();
  return (
    <label className="switch check" data-disabled={disabled || undefined}>
      <input type="checkbox" role="switch" className="check-input switch-input" checked={checked} disabled={disabled} data-testid={testId}
        aria-describedby={description ? descId : undefined} onChange={(e) => onChange(e.target.checked)} />
      <span className="switch-track" aria-hidden="true" />
      <span className="check-text">
        <span>{label}</span>
        {description && <span className="check-desc" id={descId}>{description}</span>}
      </span>
    </label>
  );
}

/** Сегменты: одна группа radio с перемещением стрелками (активен один вариант, Tab попадает на выбранный). */
export function Segmented<T extends string>({ value, options, onChange, ariaLabel, disabled, testId }: {
  value: T; options: readonly { value: T; label: string; testId?: string }[]; onChange: (v: T) => void; ariaLabel: string; disabled?: boolean; testId?: string;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const move = (i: number) => {
    const next = (i + options.length) % options.length;
    refs.current[next]?.focus();
    onChange(options[next]!.value);
  };
  return (
    <div className="seg" role="radiogroup" aria-label={ariaLabel} data-testid={testId}>
      {options.map((o, i) => (
        <button key={o.value} ref={(el) => { refs.current[i] = el; }} type="button" role="radio" className="seg-btn" aria-checked={o.value === value}
          tabIndex={o.value === value ? 0 : -1} aria-disabled={disabled || undefined} data-testid={o.testId}
          onClick={() => !disabled && o.value !== value && onChange(o.value)}
          onKeyDown={(e) => {
            if (disabled) return;
            if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { e.preventDefault(); move(i + 1); }
            if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { e.preventDefault(); move(i - 1); }
          }}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

/** Крупный выбор режима: карточки-радиокнопки. Выбор применяется только через onChange (вызывающий решает, нужен ли confirm). */
export function ChoiceCards<T extends string>({ value, options, onChange, ariaLabel, disabled }: {
  value: T; ariaLabel: string; disabled?: boolean; onChange: (v: T) => void;
  options: readonly { value: T; title: string; badge?: ReactNode; body: ReactNode; testId?: string }[];
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const move = (i: number) => {
    const next = (i + options.length) % options.length;
    refs.current[next]?.focus();
  };
  return (
    <div className="choice-grid" role="radiogroup" aria-label={ariaLabel}>
      {options.map((o, i) => (
        <button key={o.value} ref={(el) => { refs.current[i] = el; }} type="button" role="radio" className="choice" aria-checked={o.value === value}
          tabIndex={o.value === value ? 0 : -1} aria-disabled={disabled || undefined} data-testid={o.testId}
          onClick={() => !disabled && o.value !== value && onChange(o.value)}
          onKeyDown={(e) => {
            if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { e.preventDefault(); move(i + 1); }
            if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { e.preventDefault(); move(i - 1); }
          }}>
          <span className="choice-head">
            <span className="choice-radio" aria-hidden="true" />
            <span className="choice-title">{o.title}</span>
            {o.badge}
          </span>
          <span className="caption secondary">{o.body}</span>
        </button>
      ))}
    </div>
  );
}
