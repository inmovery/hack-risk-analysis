import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { Button, Checkbox, ChoiceCards, Select } from './controls';

afterEach(() => cleanup());

const options = [
  { value: 'a', label: 'Аккаунты' },
  { value: 'b', label: 'Баланс', disabled: true },
  { value: 'c', label: 'Услуги' },
  { value: 'd', label: 'Удаления' },
];

function Harness({ onChange }: { onChange?: (v: string) => void }) {
  const [v, setV] = useState<string | null>('a');
  return (
    <>
      <Select ariaLabel="Тип" value={v} options={options} testId="sel" onChange={(x) => { setV(x); onChange?.(x); }} />
      <button>после</button>
    </>
  );
}

describe('Select', () => {
  it('открывается стрелкой, пропускает недоступный вариант, Enter выбирает и возвращает фокус', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    const trigger = screen.getByTestId('sel');
    trigger.focus();
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(trigger.getAttribute('aria-activedescendant')).toMatch(/-0$/);
    fireEvent.keyDown(trigger, { key: 'ArrowDown' });
    expect(trigger.getAttribute('aria-activedescendant')).toMatch(/-2$/);
    fireEvent.keyDown(trigger, { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith('c');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(trigger);
    expect(trigger.textContent).toContain('Услуги');
  });

  it('Esc закрывает без изменений; End — последний доступный', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    const trigger = screen.getByTestId('sel');
    fireEvent.keyDown(trigger, { key: 'End' });
    expect(trigger.getAttribute('aria-activedescendant')).toMatch(/-3$/);
    fireEvent.keyDown(trigger, { key: 'Escape' });
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('поиск по первой букве: повтор буквы переходит к следующему варианту на неё', () => {
    render(<Harness />);
    const trigger = screen.getByTestId('sel');
    fireEvent.keyDown(trigger, { key: 'у' });
    expect(trigger.getAttribute('aria-activedescendant')).toMatch(/-2$/);
    fireEvent.keyDown(trigger, { key: 'у' });
    expect(trigger.getAttribute('aria-activedescendant')).toMatch(/-3$/);
  });

  it('клик по недоступному варианту ничего не выбирает; клик вне — закрывает', () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    fireEvent.click(screen.getByTestId('sel'));
    fireEvent.click(screen.getByTestId('sel-option-b'));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByTestId('sel-option-a').getAttribute('aria-selected')).toBe('true');
    fireEvent.pointerDown(screen.getByText('после'));
    expect(screen.queryByTestId('sel-list')).toBeNull();
  });

  it('недоступный список не открывается', () => {
    render(<Select ariaLabel="Тип" value={null} options={options} onChange={() => {}} disabled testId="sel" />);
    fireEvent.click(screen.getByTestId('sel'));
    fireEvent.keyDown(screen.getByTestId('sel'), { key: 'ArrowDown' });
    expect(screen.queryByTestId('sel-list')).toBeNull();
    expect(screen.getByTestId('sel').textContent).toContain('Выберите');
  });
});

describe('Checkbox', () => {
  it('нативный input: клик по подписи переключает, частичный выбор — aria-checked=mixed', () => {
    const onChange = vi.fn();
    const { rerender } = render(<Checkbox checked={false} onChange={onChange} label="Сбои доставки" testId="cb" />);
    fireEvent.click(screen.getByText('Сбои доставки'));
    expect(onChange).toHaveBeenCalledWith(true);
    rerender(<Checkbox checked={false} indeterminate onChange={onChange} label="Сбои доставки" testId="cb" />);
    const input = screen.getByTestId('cb') as HTMLInputElement;
    expect(input.indeterminate).toBe(true);
    expect(input.getAttribute('aria-checked')).toBe('mixed');
  });
});

describe('Button', () => {
  it('в загрузке держит aria-busy и не принимает повторный клик', () => {
    const onClick = vi.fn();
    render(<Button loading onClick={onClick} testId="b">Сохранить</Button>);
    const b = screen.getByTestId('b');
    expect(b.getAttribute('aria-busy')).toBe('true');
    fireEvent.click(b);
    expect(onClick).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Сохранить' })).toBeTruthy();
  });
});

describe('ChoiceCards', () => {
  it('выбор — кликом; стрелки только перемещают фокус', () => {
    const onChange = vi.fn();
    render(<ChoiceCards ariaLabel="Режим" value="ml" onChange={onChange}
      options={[{ value: 'ml', title: 'ML', body: '', testId: 'ml' }, { value: 'rules', title: 'Правила', body: '', testId: 'rules' }]} />);
    screen.getByTestId('ml').focus();
    fireEvent.keyDown(screen.getByTestId('ml'), { key: 'ArrowRight' });
    expect(document.activeElement).toBe(screen.getByTestId('rules'));
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('rules'));
    expect(onChange).toHaveBeenCalledWith('rules');
  });
});
