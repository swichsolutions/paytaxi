import type { Block, LegalDoc, LegalLang } from './legal-doc';

/**
 * The driver-facing terms of use (Georgian „სარგებლობის პირობები“), one document per language.
 * Deliberately short, modelled on the structure of paypro.ge's terms, and it refers to the privacy
 * policy instead of repeating it. Section ids are shared by all three languages, so a link like
 * /terms/en#fee points at the same section in each.
 *
 * Every statement here must stay true to what the system actually does: no wallet or balance
 * held by PayTaxi, the taxi park pays from its own bank account, Yandex debit first, cashouts
 * wait in a queue (and go out automatically) when the park's account lacks funds or the bank is
 * down, and staff can re-send a failed cashout or make one for a driver at the driver's request.
 * Only {operator} and {provider} staff can cancel a waiting cashout — never the park's manager
 * (queue-forever, licence Annex 3 §5; CashoutsController.Cancel refuses park managers).
 * A driver's PayTaxi account has one "switched off" state, suspension (DriverStatus.Suspended;
 * ka „შეჩერება“, ru «приостановка») — whether the driver stopped driving for the park, asked for
 * it, lost the phone or misused PayTaxi; the privacy policy ties retention to it. When the code
 * changes any of that, change this text in all three languages and bump TERMS_META.version. {placeholders} are filled from LEGAL_DETAILS
 * (legal-config.ts); keep them in the nominative — no case ending is attached to a placeholder.
 *
 * Georgian is the governing text; English and Russian are translations. In the Georgian text
 * „PayTaxi-ს ანგარიში“ is the driver's account in the app and plain „ანგარიში“ a bank account.
 */
export type TermsDoc = LegalDoc;

const p = (text: string): Block => ({ kind: 'p', text });
const ul = (...items: string[]): Block => ({ kind: 'ul', items });

// ═══════════════════════════════════════════════════════════════════════
//  English
// ═══════════════════════════════════════════════════════════════════════
const en: TermsDoc = {
  lang: 'en',
  langName: 'English',
  eyebrow: 'Legal',
  title: 'Terms of Use',
  subtitle: 'The agreement between you and PayTaxi for withdrawing your Yandex earnings to your bank account.',
  versionLabel: 'Version',
  effectiveLabel: 'In force from',
  draftDateLabel: 'Draft of',
  draftTag: 'Draft',
  draftBanner: 'Draft — under legal review and not yet in force. Highlighted items are still to be completed.',
  blankTitle: 'To be completed',
  blanks: {
    operator: 'legal name of the operator',
    operatorId: 'operator’s identification code',
    operatorAddress: 'operator’s registered address',
    provider: 'legal name of the platform provider',
    providerId: 'identification code',
    providerAddress: 'registered address',
    email: 'contact e-mail',
    support: 'PayTaxi support contact',
    sms: 'SMS provider',
    hosting: 'hosting provider',
    hostingLocation: 'data-centre country',
    yandexCountry: 'country of the Yandex company',
    transferBasis: 'legal basis and safeguards of each transfer',
    logDays: 'N days',
    financialYears: 'N',
  },
  toc: 'Contents',
  skipToContent: 'Skip to the terms',
  print: 'Print',
  backToApp: 'Back to PayTaxi',
  governingNote: 'This is a translation.',
  sections: [
    {
      id: 'about',
      title: 'What PayTaxi is and who provides it',
      blocks: [
        p('PayTaxi is an app that lets drivers of partner taxi parks in Georgia withdraw their Yandex earnings to their own bank account. It is provided by:'),
        ul(
          '{provider} (identification code {providerId}, {providerAddress}), which owns, develops and runs the PayTaxi platform;',
          '{operator}, which operates PayTaxi in Georgia under a licence agreement with the platform owner, connects taxi parks to PayTaxi and gives drivers first-line support. If your park was connected directly by the platform owner, {provider} does this instead.',
        ),
        p('In these terms “PayTaxi” and “we” mean these two companies, each responsible only for its own role (see “Responsibility”). Your taxi park offers PayTaxi to its drivers, sets cashout limits and pays every cashout from its own bank account; paying out your earnings this way is part of your arrangement with your park. These terms do not change your agreements with your park or with Yandex.'),
        p('PayTaxi never holds your money and keeps no wallet for you: your earnings stay on your Yandex balance until you cash out, and each cashout goes from your park’s bank account straight to yours. PayTaxi is not a Yandex product.'),
        p('The PayTaxi app and platform belong to {provider}. You may use them only to manage your own cashouts; you may not copy, modify or reverse-engineer them or access them by automated means.'),
      ],
    },
    {
      id: 'eligibility',
      title: 'Who may use PayTaxi',
      blocks: [
        p('You may use PayTaxi if you are at least 18, drive for a taxi park that uses PayTaxi and have an active driver profile in that park’s Yandex Fleet. Your park creates your PayTaxi account and links it to your Yandex profile; you do not register yourself.'),
        p('You accept these terms electronically when you sign in to PayTaxi: the sign-in screen links to them and says that signing in means accepting them, and we keep a record of each sign-in. If you do not agree, please do not use PayTaxi.'),
      ],
    },
    {
      id: 'account',
      title: 'Signing in and account security',
      blocks: [
        ul(
          'You sign in with your mobile number and a one-time SMS code. A device stays signed in for a limited period (see the Privacy Policy) unless you sign out or your account is suspended first; a new device needs a new code.',
          'Keep your phone and codes safe. Never share a code, even with someone claiming to be from PayTaxi or your park.',
          'If your phone is lost or someone else may be using your account, tell your park or PayTaxi support at once so that the account can be suspended.',
          'A cashout confirmed in your account, or made at your request (see “How a cashout works”), counts as yours unless it happened without your fault — for example after you reported your phone lost, or through a platform fault.',
        ),
      ],
    },
    {
      id: 'details',
      title: 'Your details and bank accounts',
      blocks: [
        ul(
          'Your name and phone number are registered by your park; tell it if they are wrong or change. Do not give false details, pretend to be someone else or use another person’s PayTaxi account or bank account.',
          'Cashouts go only to a bank account (IBAN) opened in Georgia at a bank your park pays out to; the app shows which. Check the IBAN carefully: you are responsible for the account number you save, and money paid to the account you chose counts as paid to you.',
          'You may add only accounts in your own name; by adding one you confirm that it is yours. The app compares the holder name you enter with the name your park registered, if any, but this does not prove who holds the account.',
          'An account in someone else’s name can be added only by your park, or by {operator} or {provider} on its behalf, at your request and with the reason recorded; the park may refuse. Money paid to such an account also counts as paid to you.',
        ),
      ],
    },
    {
      id: 'cashouts',
      title: 'How a cashout works',
      blocks: [
        ul(
          'The amount may not exceed your Yandex balance (checked again when you confirm) and must stay within your park’s limits: a minimum per cashout (5 GEL unless the park sets another) and any maximum per cashout or daily limit the park sets.',
          'Before you confirm, the app shows the fee and the amount you will receive.',
          'On confirmation the amount is first debited from your Yandex balance; then your park’s bank transfers it, minus the fee, to your chosen account. This usually takes minutes, but timing depends on the bank and is not guaranteed.',
          'Your park, or {operator} or {provider} on its behalf, may also make a cashout for you, but only at your request and only to an account saved in your profile. It works like any other cashout — same limits, fee and debit from your Yandex balance — and appears in your history and notifications; the system records who made it.',
          'For each completed cashout PayTaxi issues, in your name, an invoice to your park for the amount you received; by accepting these terms you authorise PayTaxi to do so. Your park keeps it and can give you a copy.',
        ),
      ],
    },
    {
      id: 'waiting',
      title: 'When a cashout waits, is cancelled or fails',
      blocks: [
        ul(
          'Waiting. If your park’s account lacks funds, its bank is unavailable or its payout account cannot be used for the moment, the cashout waits in a queue and goes out automatically once the cause is removed. Until it is paid or cancelled, your park still owes you the amount debited from your Yandex balance.',
          'Cancelled. A waiting cashout that the bank has not yet accepted is cancelled if you ask your park or PayTaxi support. {operator} or {provider} may also cancel a waiting cashout when the cause of the wait cannot be removed, for example when the park stops using PayTaxi. The full amount then returns to your Yandex balance.',
          'Failed. If the bank rejects the transfer, for example because the account is invalid, the full amount returns to your Yandex balance. You may then request a new cashout or ask your park to re-send the failed one: at your request your park, {operator} or {provider} re-sends it, and the amount is debited from your Yandex balance again, as for a new cashout.',
          'Under review. If it is unclear whether your Yandex balance was debited or the transfer was made, or the amount could not be returned to your Yandex balance automatically, a person checks before anything is repeated or returned, so that you are neither paid twice nor left unpaid. The cashout then either completes or its amount returns to your Yandex balance.',
          'Paid by mistake. If you receive money by mistake — for example the same cashout twice, or a cashout whose amount was also returned to your Yandex balance — you must return it. Your park may ask you to pay it back or reduce your Yandex balance by that amount.',
        ),
      ],
    },
    {
      id: 'fee',
      title: 'The fee',
      blocks: [
        ul(
          'For each completed cashout a flat fee, currently 0.50 GEL, is withheld from the amount and stays in your park’s account (the park and PayTaxi settle PayTaxi’s share between themselves): cash out 100 GEL and 99.50 GEL arrives.',
          'Cancelled and failed cashouts are free, and there is no other charge for using PayTaxi.',
          'The fee changes only as described in “Changes to these terms”. A cashout carries the fee in force when it is confirmed; the fee of a cashout already on its way does not change.',
        ),
      ],
    },
    {
      id: 'liability',
      title: 'Responsibility',
      blocks: [
        p('We are responsible for PayTaxi working as these terms describe. If a platform error causes a wrong debit or payment, we will investigate and correct it, together with your park where its account is involved.'),
        p('Apart from correcting a wrong debit or payment, our liability to you is limited to direct, proven loss and does not cover lost income. Each company is responsible only for its own role: {provider} for the platform working correctly, {operator} for support and its dealings with your park.'),
        p('PayTaxi is not responsible for:'),
        ul(
          'your work as a driver — rides, passengers, your vehicle, traffic rules and your taxes;',
          'how Yandex and your park calculate your earnings and balance;',
          'whether your park keeps enough money in its bank account;',
          'outages of banks, Yandex, mobile networks or the internet, or brief maintenance pauses: cashouts held up by the bank or a lack of funds wait in the queue and go out automatically, and while Yandex is unreachable no new cashout can be made;',
          'events beyond our reasonable control (force majeure), such as natural disasters, war, large-scale power cuts, cyber-attacks or changes in law;',
          'losses caused by sharing your codes or giving false details.',
        ),
        p('Nothing in these terms limits liability that Georgian law does not allow to be limited, such as for harm caused intentionally or by gross negligence.'),
      ],
    },
    {
      id: 'termination',
      title: 'Suspension and termination',
      blocks: [
        ul(
          'You may stop using PayTaxi at any time; to have your PayTaxi account suspended, ask your park or PayTaxi support.',
          'Your park suspends your PayTaxi account when you stop driving for it, at your request or when your phone is lost (see “Signing in and account security”). It may also suspend it for false details, fraud or attempted fraud, use of another person’s identity or account, or misuse that endangers other users or the platform; {operator} or {provider} may do so on your park’s behalf when they detect such conduct. While your account is suspended you cannot sign in or cash out, and the app tells you so; you can ask for the reason, and your park can reactivate it.',
          'Your park’s use of PayTaxi may be suspended under its agreement for using PayTaxi; new cashouts are then paused, while cashouts already waiting continue as described above.',
          'Suspending an account does not stop a cashout already on its way: it is completed or, if it is cancelled or fails, its amount returns to your Yandex balance.',
          'If your park stops using PayTaxi, we will tell you in the app in advance where we can. New cashouts then stop, cashouts already on their way are completed or cancelled, and your data is handled as the Privacy Policy describes.',
          'If the operator’s licence agreement ends, the operator’s role, including its rights and duties under these terms, passes to the platform owner or to a company it names; we will tell you in the app, and these terms continue to apply.',
        ),
      ],
    },
    {
      id: 'privacy',
      title: 'Personal data and messages',
      blocks: [
        p('Your park is the controller of your personal data processed in PayTaxi. {provider} processes it on the park’s behalf, as a processor, only to provide cashouts; {operator} accesses it on the park’s behalf for onboarding and support. The exception is the record of your sign-ins, which shows that you accepted these terms: {provider} and {operator} keep it on their own account. Details and your rights are in the PayTaxi Privacy Policy, linked from the sign-in page.'),
        p('We send SMS only with sign-in codes; messages about cashouts appear in the app. We send no advertising or marketing messages.'),
      ],
    },
    {
      id: 'changes',
      title: 'Changes to these terms',
      blocks: [
        p('We change these terms only when the law, the rules of the banks or Yandex, or PayTaxi’s security or features change, or to change the fee. We will tell you in the app at least 7 calendar days before a change takes effect. If you do not agree, you may stop using PayTaxi before then. A cashout confirmed after the change takes effect is made on the new terms; one confirmed earlier is completed on the terms in force when it was confirmed.'),
      ],
    },
    {
      id: 'contact',
      title: 'Contact, disputes and governing law',
      blocks: [
        p('Questions about a cashout, your balance or your account: your park, or PayTaxi support: {support}. Support passes technical issues to the platform owner. Requests about your personal data: your park or {email}. By post: {provider}, {providerAddress}.'),
        p('We will try to resolve any complaint or disagreement by negotiation; failing that, the dispute goes to the competent court of Georgia. These terms are governed by Georgian law. If the Georgian, English and Russian versions differ, the Georgian text prevails.'),
      ],
    },
  ],
  footer: 'PayTaxi platform: {provider}. Operator in Georgia: {operator}.',
};

// ═══════════════════════════════════════════════════════════════════════
//  ქართული (governing text)
// ═══════════════════════════════════════════════════════════════════════
const ka: TermsDoc = {
  lang: 'ka',
  langName: 'ქართული',
  eyebrow: 'იურიდიული ინფორმაცია',
  title: 'სარგებლობის პირობები',
  subtitle: 'შეთანხმება თქვენსა და PayTaxi-ს შორის Yandex-ში გამომუშავებული თანხის თქვენს საბანკო ანგარიშზე გამოტანის შესახებ.',
  versionLabel: 'ვერსია',
  effectiveLabel: 'ძალაში შესვლის თარიღი:',
  draftDateLabel: 'პროექტი,',
  draftTag: 'პროექტი',
  draftBanner: 'პროექტი — მიმდინარეობს იურიდიული შემოწმება, ჯერ ძალაში არ არის. მონიშნული ველები ჯერ შესავსებია.',
  blankTitle: 'შესავსებია',
  blanks: {
    operator: 'ოპერატორის იურიდიული სახელწოდება',
    operatorId: 'ოპერატორის საიდენტიფიკაციო კოდი',
    operatorAddress: 'ოპერატორის იურიდიული მისამართი',
    provider: 'პლატფორმის მომწოდებლის სახელწოდება',
    providerId: 'საიდენტიფიკაციო კოდი',
    providerAddress: 'იურიდიული მისამართი',
    email: 'საკონტაქტო ელფოსტა',
    support: 'PayTaxi-ს მხარდაჭერის კონტაქტი',
    sms: 'SMS მომწოდებელი',
    hosting: 'ჰოსტინგის მომწოდებელი',
    hostingLocation: 'მონაცემთა ცენტრის ქვეყანა',
    yandexCountry: 'Yandex-ის კომპანიის ქვეყანა',
    transferBasis: 'თითოეული გადაცემის სამართლებრივი საფუძველი და გარანტიები',
    logDays: 'N დღე',
    financialYears: 'N',
  },
  toc: 'სარჩევი',
  skipToContent: 'პირობების ტექსტზე გადასვლა',
  print: 'ბეჭდვა',
  backToApp: 'PayTaxi-ში დაბრუნება',
  governingNote: '',
  sections: [
    {
      id: 'about',
      title: 'რა არის PayTaxi და ვინ გიწევთ მომსახურებას',
      blocks: [
        p('PayTaxi არის აპლიკაცია, რომლითაც საქართველოში პარტნიორი ტაქსოპარკების მძღოლებს შეუძლიათ Yandex-ში გამომუშავებული თანხა საკუთარ საბანკო ანგარიშზე გამოიტანონ. მომსახურებას გიწევენ:'),
        ul(
          '{provider} (საიდენტიფიკაციო კოდი {providerId}, {providerAddress}) — PayTaxi-ს პლატფორმის მფლობელი, რომელიც პლატფორმას ავითარებს და უზრუნველყოფს მის მუშაობას;',
          '{operator} — პლატფორმის მფლობელთან დადებული სალიცენზიო ხელშეკრულების საფუძველზე საქართველოში PayTaxi-ს ოპერატორი: ტაქსოპარკებს აერთებს PayTaxi-სთან და მძღოლებს უწევს პირველი ხაზის მხარდაჭერას. თუ თქვენი პარკი PayTaxi-სთან უშუალოდ პლატფორმის მფლობელმა დააკავშირა, ამ ფუნქციებს ასრულებს {provider}.',
        ),
        p('ამ პირობებში „PayTaxi“ და „ჩვენ“ ნიშნავს ამ ორ კომპანიას, რომელთაგან თითოეული პასუხს აგებს მხოლოდ საკუთარი როლის ფარგლებში (იხ. „პასუხისმგებლობა“). თქვენი ტაქსოპარკი მძღოლებს სთავაზობს PayTaxi-ს, ადგენს გამოტანის ლიმიტებს და ყოველი გამოტანის თანხას საკუთარი საბანკო ანგარიშიდან რიცხავს; გამომუშავებული თანხის ამ გზით გაცემა თქვენსა და პარკს შორის შეთანხმების ნაწილია. ეს პირობები არ ცვლის პარკთან და Yandex-თან დადებულ თქვენს შეთანხმებებს.'),
        p('PayTaxi თქვენს ფულს არასდროს ფლობს და PayTaxi-ში თქვენი საფულე არ არსებობს: გამომუშავებული თანხა გამოტანამდე თქვენს Yandex-ის ბალანსზე რჩება, ყოველი გამოტანა კი პარკის საბანკო ანგარიშიდან პირდაპირ თქვენს საბანკო ანგარიშზე ირიცხება. PayTaxi არ არის Yandex-ის პროდუქტი.'),
        p('PayTaxi-ს აპლიკაციისა და პლატფორმის მფლობელია {provider}. მათი გამოყენება შეგიძლიათ მხოლოდ საკუთარი გამოტანების სამართავად; დაუშვებელია მათი კოპირება, შეცვლა, დეკომპილაცია ან ავტომატიზებული საშუალებებით მათზე წვდომა.'),
      ],
    },
    {
      id: 'eligibility',
      title: 'ვის შეუძლია PayTaxi-თ სარგებლობა',
      blocks: [
        p('PayTaxi-თ სარგებლობა შეგიძლიათ, თუ 18 წლის ან უფროსი ხართ, თანამშრომლობთ ტაქსოპარკთან, რომელიც PayTaxi-ს იყენებს, და ამ პარკის Yandex Fleet-ში გაქვთ აქტიური მძღოლის პროფილი. PayTaxi-ს ანგარიშს გიქმნით პარკი და აკავშირებს მას თქვენს Yandex-ის პროფილთან; თავად რეგისტრაცია არ გჭირდებათ.'),
        p('ამ პირობებს ელექტრონულად ეთანხმებით PayTaxi-ში შესვლისას: შესვლის გვერდზე მითითებულია მათი ბმული და ის, რომ სისტემაში შესვლა მათზე თანხმობას ნიშნავს, ჩვენ კი ყოველ შესვლას ვაღრიცხავთ. თუ არ ეთანხმებით, გთხოვთ, არ ისარგებლოთ PayTaxi-თ.'),
      ],
    },
    {
      id: 'account',
      title: 'სისტემაში შესვლა და PayTaxi-ს ანგარიშის უსაფრთხოება',
      blocks: [
        ul(
          'სისტემაში შედიხართ მობილური ნომრითა და ერთჯერადი SMS კოდით. მოწყობილობა სისტემაში რჩება შეზღუდული ვადით (იხ. კონფიდენციალურობის პოლიტიკა), თუ მანამდე არ გამოხვალთ ან თქვენი PayTaxi-ს ანგარიში არ შეჩერდება; ახალ მოწყობილობაზე ახალი კოდი დაგჭირდებათ.',
          'დაიცავით ტელეფონი და კოდები. კოდი არავის გაუზიაროთ, მათ შორის იმას, ვინც თავს PayTaxi-ს ან თქვენი პარკის წარმომადგენლად წარმოგიდგენთ.',
          'თუ ტელეფონი დაკარგეთ ან თქვენს PayTaxi-ს ანგარიშს შესაძლოა სხვა იყენებდეს, დაუყოვნებლივ აცნობეთ პარკს ან PayTaxi-ს მხარდაჭერას, რომ ის შეჩერდეს.',
          'თქვენს PayTaxi-ს ანგარიშში დადასტურებული გამოტანა, ისევე როგორც თქვენი მოთხოვნით შესრულებული გამოტანა (იხ. „როგორ ხდება გამოტანა“), თქვენ მიერ შესრულებულად ითვლება, გარდა იმ შემთხვევისა, როცა ის თქვენი ბრალის გარეშე მოხდა — მაგალითად, ტელეფონის დაკარგვის შესახებ შეტყობინების შემდეგ ან პლატფორმის ხარვეზის გამო.',
        ),
      ],
    },
    {
      id: 'details',
      title: 'თქვენი მონაცემები და საბანკო ანგარიშები',
      blocks: [
        ul(
          'თქვენს სახელსა და ტელეფონის ნომერს არეგისტრირებს პარკი; თუ ისინი არასწორია ან შეიცვალა, აცნობეთ მას. დაუშვებელია ყალბი მონაცემების მითითება, სხვა პირად წარდგენა და სხვისი PayTaxi-ს ანგარიშის ან საბანკო ანგარიშის გამოყენება.',
          'გამოტანის თანხა ირიცხება მხოლოდ საქართველოში გახსნილ საბანკო ანგარიშზე (IBAN) იმ ბანკში, რომლის ანგარიშებზეც თქვენი პარკი რიცხავს თანხას; ეს ბანკები აპლიკაციაშია მითითებული. ანგარიშის ნომერი ყურადღებით გადაამოწმეთ: შენახული ანგარიშის ნომრის სისწორეზე პასუხს აგებთ თქვენ, ხოლო თქვენ მიერ არჩეულ ანგარიშზე ჩარიცხული თანხა თქვენთვის გადახდილად ითვლება.',
          'შეგიძლიათ დაამატოთ მხოლოდ თქვენს სახელზე გახსნილი ანგარიში; მისი დამატებით ადასტურებთ, რომ ის თქვენია. აპლიკაცია თქვენ მიერ მითითებულ მფლობელის სახელს ადარებს პარკის მიერ დარეგისტრირებულ სახელს (თუ ასეთი არსებობს), თუმცა ეს არ ადასტურებს, ვინ ფლობს ანგარიშს.',
          'სხვა პირის სახელზე გახსნილ ანგარიშს ამატებს მხოლოდ პარკი ან მისი სახელით {operator} ან {provider} — თქვენი მოთხოვნით და მიზეზის აღრიცხვით; პარკს შეუძლია უარი გითხრათ. ასეთ ანგარიშზე ჩარიცხული თანხაც თქვენთვის გადახდილად ითვლება.',
        ),
      ],
    },
    {
      id: 'cashouts',
      title: 'როგორ ხდება გამოტანა',
      blocks: [
        ul(
          'თანხა არ უნდა აღემატებოდეს თქვენს Yandex-ის ბალანსს (დადასტურებისას ის ხელახლა მოწმდება) და არ უნდა სცდებოდეს პარკის ლიმიტებს: ერთი გამოტანის მინიმუმს (5 ლარი, თუ პარკმა სხვა არ დაადგინა) და, თუ პარკმა დააწესა, ერთი გამოტანის მაქსიმუმსა და დღიურ ლიმიტს.',
          'დადასტურებამდე აპლიკაცია გაჩვენებთ საკომისიოს და თანხას, რომელსაც მიიღებთ.',
          'დადასტურებისას თანხა ჯერ ჩამოიწერება თქვენი Yandex-ის ბალანსიდან, შემდეგ კი პარკის ბანკი ამ თანხას, საკომისიოს გამოკლებით, არჩეულ ანგარიშზე რიცხავს. ეს ჩვეულებრივ რამდენიმე წუთს გრძელდება, თუმცა ვადა ბანკზეა დამოკიდებული და გარანტირებული არ არის.',
          'თქვენი პარკი ან მისი სახელით {operator} ან {provider} უფლებამოსილია, თქვენ ნაცვლად განახორციელოს გამოტანა, მაგრამ მხოლოდ თქვენი მოთხოვნით და მხოლოდ თქვენს პროფილში შენახულ ანგარიშზე. ასეთი გამოტანა სხვა გამოტანების მსგავსად სრულდება — იგივე ლიმიტებით, საკომისიოთი და Yandex-ის ბალანსიდან ჩამოწერით — და ჩანს თქვენს ისტორიასა და შეტყობინებებში; სისტემა აღრიცხავს, ვინ შეასრულა ის.',
          'ყოველ შესრულებულ გამოტანაზე PayTaxi თქვენი სახელით პარკზე გამოწერს ინვოისს თქვენ მიერ მიღებულ თანხაზე; ამ პირობებზე დათანხმებით PayTaxi-ს ამის უფლებამოსილებას ანიჭებთ. ინვოისს ინახავს პარკი, ასლის მიღება კი მისგან შეგიძლიათ.',
        ),
      ],
    },
    {
      id: 'waiting',
      title: 'როცა გამოტანა რიგშია, უქმდება ან ვერ სრულდება',
      blocks: [
        ul(
          'ლოდინი. თუ პარკის ანგარიშზე თანხა საკმარისი არ არის, მისი ბანკი მიუწვდომელია ან პარკის ანგარიშიდან გადარიცხვა დროებით შეუძლებელია, გამოტანა რიგში დგება და მიზეზის აღმოფხვრისთანავე ავტომატურად სრულდება. სანამ გამოტანა არ შესრულდება ან არ გაუქმდება, თქვენი Yandex-ის ბალანსიდან ჩამოწერილი თანხის გადახდის ვალდებულება პარკს რჩება.',
          'გაუქმება. მომლოდინე გამოტანა, რომელიც ბანკს ჯერ არ მიუღია, უქმდება, თუ ამას პარკს ან PayTaxi-ს მხარდაჭერას სთხოვთ. {operator} ან {provider} უფლებამოსილია, გააუქმოს მომლოდინე გამოტანა ასევე მაშინ, როცა ლოდინის მიზეზის აღმოფხვრა შეუძლებელია, მაგალითად, თუ პარკი PayTaxi-თ სარგებლობას წყვეტს. ამ შემთხვევაში მთელი თანხა თქვენს Yandex-ის ბალანსს უბრუნდება.',
          'ვერ შესრულებული გამოტანა. თუ ბანკი გადარიცხვაზე უარს ამბობს, მაგალითად, არასწორი ანგარიშის გამო, მთელი თანხა თქვენს Yandex-ის ბალანსს უბრუნდება. ამის შემდეგ შეგიძლიათ ახალი გამოტანა მოითხოვოთ ან სთხოვოთ პარკს ვერ შესრულებული გამოტანის ხელახლა გაგზავნა: თქვენი მოთხოვნით მას ხელახლა აგზავნის პარკი, {operator} ან {provider}, და თანხა თქვენი Yandex-ის ბალანსიდან კვლავ ჩამოიწერება, როგორც ახალი გამოტანისას.',
          'გადამოწმება. თუ გაურკვეველია, ჩამოიწერა თუ არა თანხა თქვენი Yandex-ის ბალანსიდან ან შესრულდა თუ არა გადარიცხვა, ან თანხის Yandex-ის ბალანსზე ავტომატურად დაბრუნება ვერ მოხერხდა, სანამ რაიმე განმეორდება ან დაბრუნდება, გამოტანა ხელით მოწმდება, რათა თანხა არც ორჯერ ჩაგერიცხოთ და არც დაიკარგოს. შემდეგ გამოტანა ან სრულდება, ან მისი თანხა თქვენს Yandex-ის ბალანსს უბრუნდება.',
          'შეცდომით ჩარიცხვა. თუ თანხა შეცდომით ჩაგერიცხათ — მაგალითად, ერთი და იგივე გამოტანა ორჯერ, ან გამოტანა, რომლის თანხაც თქვენს Yandex-ის ბალანსსაც დაუბრუნდა, — ვალდებული ხართ, ის დააბრუნოთ. პარკს შეუძლია, მოითხოვოს თანხის დაბრუნება ან ამ თანხით შეამციროს თქვენი Yandex-ის ბალანსი.',
        ),
      ],
    },
    {
      id: 'fee',
      title: 'საკომისიო',
      blocks: [
        ul(
          'ყოველ შესრულებულ გამოტანაზე თანხას აკლდება ფიქსირებული საკომისიო, ამჟამად 50 თეთრი, რომელიც პარკის ანგარიშზე რჩება (PayTaxi-ს წილზე ანგარიშსწორება პარკსა და PayTaxi-ს შორის ცალკე ხდება): 100 ლარის გამოტანისას ჩაგერიცხებათ 99.50 ლარი.',
          'გაუქმებული და ვერ შესრულებული გამოტანა უფასოა; PayTaxi-თ სარგებლობისთვის სხვა საფასური არ არსებობს.',
          'საკომისიო იცვლება მხოლოდ „პირობების ცვლილების“ ნაწილში აღწერილი წესით. გამოტანაზე მოქმედებს მისი დადასტურების მომენტში მოქმედი საკომისიო; უკვე დაწყებული გამოტანის საკომისიო აღარ იცვლება.',
        ),
      ],
    },
    {
      id: 'liability',
      title: 'პასუხისმგებლობა',
      blocks: [
        p('ჩვენ პასუხს ვაგებთ იმაზე, რომ PayTaxi მუშაობდეს ამ პირობებში აღწერილი წესით. თუ პლატფორმის ხარვეზის გამო თანხა არასწორად ჩამოგეწერათ ან ჩაგერიცხათ, შევისწავლით და გამოვასწორებთ — თუ საქმე პარკის ანგარიშს ეხება, პარკთან ერთად.'),
        p('გარდა არასწორი ჩამოწერის ან ჩარიცხვის გასწორებისა, ჩვენი პასუხისმგებლობა თქვენ წინაშე შემოიფარგლება პირდაპირი, დადასტურებული ზიანით და არ მოიცავს მიუღებელ შემოსავალს. თითოეული კომპანია პასუხს აგებს მხოლოდ თავისი როლის ფარგლებში: {provider} — პლატფორმის გამართულ მუშაობაზე, {operator} — მხარდაჭერასა და თქვენს პარკთან ურთიერთობაზე.'),
        p('PayTaxi პასუხს არ აგებს:'),
        ul(
          'თქვენს, როგორც მძღოლის, საქმიანობაზე — მგზავრობებზე, მგზავრებთან ურთიერთობაზე, ავტომობილზე, საგზაო მოძრაობის წესებსა და თქვენს გადასახადებზე;',
          'იმაზე, როგორ ითვლიან Yandex და თქვენი პარკი თქვენს შემოსავალსა და ბალანსს;',
          'იმაზე, ინახავს თუ არა პარკი საბანკო ანგარიშზე საკმარის თანხას;',
          'ბანკების, Yandex-ის, მობილური ქსელისა და ინტერნეტის გაუმართაობაზე ან ტექნიკური სამუშაოებით გამოწვეულ ხანმოკლე შეფერხებაზე: ბანკის ან თანხის უკმარისობის გამო შეფერხებული გამოტანა რიგში დგება და ავტომატურად სრულდება, ხოლო სანამ Yandex მიუწვდომელია, ახალი გამოტანა ვერ განხორციელდება;',
          'ჩვენი გონივრული კონტროლის მიღმა მყოფ მოვლენებზე (ფორს-მაჟორი), როგორიცაა სტიქიური მოვლენები, საომარი მოქმედებები, ელექტროენერგიის მასშტაბური გათიშვა, კიბერშეტევა ან კანონმდებლობის ცვლილება;',
          'ზიანზე, რომელიც გამოწვეულია კოდების გაზიარებით ან ყალბი მონაცემების მითითებით.',
        ),
        p('ეს პირობები არ ზღუდავს პასუხისმგებლობას, რომლის შეზღუდვასაც საქართველოს კანონმდებლობა არ უშვებს, მაგალითად, განზრახ ან უხეში გაუფრთხილებლობით მიყენებული ზიანისთვის.'),
      ],
    },
    {
      id: 'termination',
      title: 'შეჩერება და შეწყვეტა',
      blocks: [
        ul(
          'PayTaxi-თ სარგებლობის შეწყვეტა ნებისმიერ დროს შეგიძლიათ; PayTaxi-ს ანგარიშის შესაჩერებლად მიმართეთ პარკს ან PayTaxi-ს მხარდაჭერას.',
          'პარკი აჩერებს თქვენს PayTaxi-ს ანგარიშს, თუ მასთან თანამშრომლობას შეწყვეტთ, თქვენი მოთხოვნით ან ტელეფონის დაკარგვისას (იხ. „სისტემაში შესვლა და PayTaxi-ს ანგარიშის უსაფრთხოება“). პარკს შეუძლია შეაჩეროს ის ასევე ყალბი მონაცემების მითითების, თაღლითობის ან მისი მცდელობის, სხვა პირის ვინაობის, PayTaxi-ს ანგარიშის ან საბანკო ანგარიშის გამოყენების ან PayTaxi-ს ბოროტად გამოყენების გამო, რაც საფრთხეს უქმნის სხვა მომხმარებლებს ან პლატფორმას; ასეთი ქმედების აღმოჩენისას {operator} ან {provider} უფლებამოსილია, ეს პარკის სახელით გააკეთოს. შეჩერებული ანგარიშით სისტემაში ვერ შეხვალთ და თანხას ვერ გამოიტანთ — ამას აპლიკაცია გაცნობებთ; შეგიძლიათ მოითხოვოთ მიზეზის განმარტება, ანგარიშის აღდგენა კი პარკს შეუძლია.',
          'თქვენი პარკის მიერ PayTaxi-თ სარგებლობა შეიძლება შეჩერდეს PayTaxi-თ სარგებლობის შესახებ მისი ხელშეკრულების საფუძველზე: ახალი გამოტანები ჩერდება, უკვე რიგში მყოფი გამოტანები კი ზემოთ აღწერილი წესით გრძელდება.',
          'PayTaxi-ს ანგარიშის შეჩერება არ აჩერებს უკვე დაწყებულ გამოტანას: ის ან შესრულდება, ან, თუ გაუქმდება ან ვერ შესრულდება, მისი თანხა თქვენს Yandex-ის ბალანსს დაუბრუნდება.',
          'თუ თქვენი პარკი PayTaxi-თ სარგებლობას შეწყვეტს, ამის შესახებ, შესაძლებლობის ფარგლებში, წინასწარ გაცნობებთ აპლიკაციაში. ამ შემთხვევაში ახალი გამოტანები ჩერდება, უკვე დაწყებული გამოტანები ან სრულდება, ან უქმდება, თქვენი მონაცემები კი მუშავდება კონფიდენციალურობის პოლიტიკის შესაბამისად.',
          'თუ ოპერატორის სალიცენზიო ხელშეკრულება შეწყდება, ოპერატორის ფუნქციები, მათ შორის ამ პირობებით მინიჭებული უფლება-მოვალეობები, გადადის პლატფორმის მფლობელზე ან მის მიერ განსაზღვრულ კომპანიაზე; ამის შესახებ გაცნობებთ აპლიკაციაში და ეს პირობები ძალაში რჩება.',
        ),
      ],
    },
    {
      id: 'privacy',
      title: 'პერსონალური მონაცემები და შეტყობინებები',
      blocks: [
        p('PayTaxi-ში თქვენი პერსონალური მონაცემების დამუშავებისთვის პასუხისმგებელი პირია თქვენი პარკი. {provider} მათ ამუშავებს პარკის სახელით, როგორც დამუშავებაზე უფლებამოსილი პირი, მხოლოდ გამოტანის მომსახურების მიზნით; {operator} მათზე წვდომას იღებს პარკის სახელით — მძღოლების ჩართვისა და მხარდაჭერისთვის. გამონაკლისია თქვენი შესვლის ისტორია, რომელიც ადასტურებს, რომ ეს პირობები მიიღეთ: მას {provider} და {operator} საკუთარი სახელით ინახავენ. დეტალები და თქვენი უფლებები აღწერილია PayTaxi-ს კონფიდენციალურობის პოლიტიკაში, რომლის ბმულიც სისტემაში შესვლის გვერდზეა.'),
        p('SMS-ით გიგზავნით მხოლოდ შესვლის კოდებს; შეტყობინებები გამოტანების შესახებ აპლიკაციაში ჩანს. სარეკლამო ან მარკეტინგულ შეტყობინებებს არ გიგზავნით.'),
      ],
    },
    {
      id: 'changes',
      title: 'პირობების ცვლილება',
      blocks: [
        p('ამ პირობებს ვცვლით მხოლოდ კანონმდებლობის, ბანკების ან Yandex-ის წესების, PayTaxi-ს უსაფრთხოების მოთხოვნების ან ფუნქციების ცვლილებისას, ან საკომისიოს შესაცვლელად. ცვლილების შესახებ აპლიკაციაში გაცნობებთ ძალაში შესვლამდე არანაკლებ 7 კალენდარული დღით ადრე. თუ არ ეთანხმებით, შეგიძლიათ მანამდე შეწყვიტოთ PayTaxi-თ სარგებლობა. ცვლილების ძალაში შესვლის შემდეგ დადასტურებული გამოტანა სრულდება ახალი პირობებით, მანამდე დადასტურებული კი — დადასტურების დროს მოქმედი პირობებით.'),
      ],
    },
    {
      id: 'contact',
      title: 'კონტაქტი, დავები და მარეგულირებელი კანონმდებლობა',
      blocks: [
        p('გამოტანის, ბალანსის ან PayTaxi-ს ანგარიშის შესახებ კითხვებით მიმართეთ თქვენს პარკს ან PayTaxi-ს მხარდაჭერას: {support}. ტექნიკურ საკითხებს მხარდაჭერა პლატფორმის მფლობელს გადასცემს. პერსონალურ მონაცემებთან დაკავშირებული მოთხოვნები: თქვენი პარკი ან {email}. საფოსტო მისამართი: {provider}, {providerAddress}.'),
        p('ნებისმიერ საჩივარსა თუ უთანხმოებას შევეცდებით მოლაპარაკებით გადავწყვიტოთ; თუ ეს ვერ მოხერხდა, დავას განიხილავს საქართველოს უფლებამოსილი სასამართლო. ეს პირობები რეგულირდება საქართველოს კანონმდებლობით. ქართულ, ინგლისურ და რუსულ ვერსიებს შორის შეუსაბამობის შემთხვევაში უპირატესობა ენიჭება ქართულ ტექსტს.'),
      ],
    },
  ],
  footer: 'PayTaxi-ს პლატფორმა: {provider}. ოპერატორი საქართველოში: {operator}.',
};

// ═══════════════════════════════════════════════════════════════════════
//  Русский
// ═══════════════════════════════════════════════════════════════════════
const ru: TermsDoc = {
  lang: 'ru',
  langName: 'Русский',
  eyebrow: 'Правовая информация',
  title: 'Условия использования',
  subtitle: 'Соглашение между вами и PayTaxi о выводе заработка в Yandex на ваш банковский счёт.',
  versionLabel: 'Версия',
  effectiveLabel: 'Действуют с',
  draftDateLabel: 'Проект от',
  draftTag: 'Проект',
  draftBanner: 'Проект — проходит юридическую проверку и ещё не действует. Выделенные поля ещё нужно заполнить.',
  blankTitle: 'Нужно заполнить',
  blanks: {
    operator: 'наименование оператора',
    operatorId: 'идентификационный код оператора',
    operatorAddress: 'юридический адрес оператора',
    provider: 'наименование поставщика платформы',
    providerId: 'идентификационный код',
    providerAddress: 'юридический адрес',
    email: 'контактный e-mail',
    support: 'контакт поддержки PayTaxi',
    sms: 'SMS-провайдер',
    hosting: 'хостинг-провайдер',
    hostingLocation: 'страна дата-центра',
    yandexCountry: 'страна компании Yandex',
    transferBasis: 'правовое основание и гарантии каждой передачи',
    logDays: 'N дней',
    financialYears: 'N',
  },
  toc: 'Содержание',
  skipToContent: 'Перейти к тексту условий',
  print: 'Печать',
  backToApp: 'Вернуться в PayTaxi',
  governingNote: 'Это перевод.',
  sections: [
    {
      id: 'about',
      title: 'Что такое PayTaxi и кто его предоставляет',
      blocks: [
        p('PayTaxi — приложение, с помощью которого водители таксопарков-партнёров в Грузии выводят заработок в Yandex на свой банковский счёт. Сервис предоставляют:'),
        ul(
          '{provider} — владелец платформы PayTaxi, который её разрабатывает и обеспечивает её работу (идентификационный код {providerId}, {providerAddress});',
          '{operator} — оператор PayTaxi в Грузии по лицензионному договору с владельцем платформы: подключает таксопарки к PayTaxi и оказывает водителям первичную поддержку. Если ваш парк подключил непосредственно владелец платформы, эти функции выполняет {provider}.',
        ),
        p('В этих условиях «PayTaxi» и «мы» означают эти две компании, каждая из которых отвечает только за свою часть (см. «Ответственность»). Ваш таксопарк подключает PayTaxi для своих водителей, устанавливает лимиты вывода и перечисляет каждый вывод со своего банковского счёта; такая выплата заработка — часть ваших договорённостей с парком. Эти условия не меняют ваших договоров с парком и с Yandex.'),
        p('PayTaxi никогда не хранит ваши деньги, и кошелька в PayTaxi у вас нет: заработок остаётся на вашем балансе в Yandex до вывода, а каждый вывод поступает с банковского счёта парка прямо на ваш счёт. PayTaxi не является продуктом Yandex.'),
        p('Приложение и платформа PayTaxi принадлежат {provider}. Вы можете пользоваться ими только для своих выводов; запрещается копировать, изменять, декомпилировать их или обращаться к ним автоматизированными средствами.'),
      ],
    },
    {
      id: 'eligibility',
      title: 'Кто может пользоваться PayTaxi',
      blocks: [
        p('Пользоваться PayTaxi можно, если вам исполнилось 18 лет, вы сотрудничаете с таксопарком, подключённым к PayTaxi, и у вас есть активный профиль водителя в Yandex Fleet этого парка. Аккаунт PayTaxi создаёт парк и привязывает его к вашему профилю в Yandex; регистрироваться самостоятельно не нужно.'),
        p('Вы принимаете эти условия в электронной форме при входе в PayTaxi: на странице входа есть ссылка на них и указано, что вход означает их принятие, а каждый вход мы фиксируем. Если вы не согласны, пожалуйста, не пользуйтесь PayTaxi.'),
      ],
    },
    {
      id: 'account',
      title: 'Вход и безопасность аккаунта',
      blocks: [
        ul(
          'Вы входите по номеру мобильного телефона и одноразовому SMS-коду. Устройство остаётся в системе ограниченное время (см. Политику конфиденциальности), если до этого вы не выйдете или ваш аккаунт не будет приостановлен; на новом устройстве нужен новый код.',
          'Берегите телефон и коды. Никому не сообщайте код, даже тому, кто представляется сотрудником PayTaxi или вашего парка.',
          'Если телефон потерян или вашим аккаунтом может пользоваться кто-то другой, сразу сообщите в парк или в поддержку PayTaxi, чтобы аккаунт приостановили.',
          'Вывод, подтверждённый в вашем аккаунте или сделанный по вашей просьбе (см. «Как проходит вывод»), считается сделанным вами, кроме случаев, когда он произошёл не по вашей вине, — например, после того как вы сообщили о потере телефона, или из-за сбоя платформы.',
        ),
      ],
    },
    {
      id: 'details',
      title: 'Ваши данные и банковские счета',
      blocks: [
        ul(
          'Ваше имя и номер телефона регистрирует парк; если они неверны или изменились, сообщите ему. Запрещается указывать ложные данные, выдавать себя за другое лицо, пользоваться чужим аккаунтом PayTaxi или чужим банковским счётом.',
          'Выводы зачисляются только на банковский счёт (IBAN), открытый в Грузии в банке, на счета в котором ваш парк делает выплаты; эти банки указаны в приложении. Внимательно проверяйте IBAN: за правильность сохранённого номера счёта отвечаете вы, а сумма, зачисленная на выбранный вами счёт, считается выплаченной вам.',
          'Добавить можно только счёт на своё имя; добавляя счёт, вы подтверждаете, что он ваш. Приложение сверяет указанное вами имя владельца с именем, которое зарегистрировал парк (если оно есть), но это не подтверждает, кому принадлежит счёт.',
          'Счёт на имя другого человека может добавить только парк или от его имени {operator} или {provider} — по вашей просьбе и с указанием причины; парк вправе отказать. Сумма, зачисленная на такой счёт, также считается выплаченной вам.',
        ),
      ],
    },
    {
      id: 'cashouts',
      title: 'Как проходит вывод',
      blocks: [
        ul(
          'Сумма не может превышать ваш баланс в Yandex (при подтверждении он проверяется заново) и должна укладываться в лимиты парка: минимальная сумма вывода (5 лари, если парк не установил другую), а также максимальная сумма вывода и дневной лимит, если парк их установил.',
          'Перед подтверждением приложение показывает комиссию и сумму, которую вы получите.',
          'После подтверждения сумма сначала списывается с вашего баланса в Yandex, затем банк парка переводит её за вычетом комиссии на выбранный счёт. Обычно это занимает несколько минут, но срок зависит от банка и не гарантируется.',
          'Ваш парк или от его имени {operator} или {provider} вправе сделать вывод за вас, но только по вашей просьбе и только на счёт, сохранённый в вашем профиле. Такой вывод проходит как любой другой — с теми же лимитами, комиссией и списанием с баланса в Yandex — и виден в вашей истории и уведомлениях; система фиксирует, кто его оформил.',
          'По каждому выполненному выводу PayTaxi от вашего имени выставляет парку счёт (инвойс) на полученную вами сумму; принимая эти условия, вы уполномочиваете PayTaxi на это. Счёт хранится у парка, и вы можете получить у него копию.',
        ),
      ],
    },
    {
      id: 'waiting',
      title: 'Если вывод ожидает, отменён или не выполнен',
      blocks: [
        ul(
          'Ожидание. Если на счёте парка недостаточно средств, его банк недоступен или счёт парка временно нельзя использовать, вывод ставится в очередь и выполняется автоматически, как только причина будет устранена. Пока вывод не выполнен или не отменён, обязанность выплатить вам списанную с баланса в Yandex сумму сохраняется за парком.',
          'Отмена. Ожидающий вывод, который банк ещё не принял, отменяется, если вы попросите об этом парк или поддержку PayTaxi. {operator} или {provider} вправе также отменить ожидающий вывод, если причину ожидания устранить невозможно, например если парк прекращает пользоваться PayTaxi. Тогда вся сумма возвращается на ваш баланс в Yandex.',
          'Отказ банка. Если банк отказывает в переводе, например из-за неверного счёта, вся сумма возвращается на ваш баланс в Yandex. Затем вы можете запросить новый вывод или попросить парк повторить невыполненный: по вашей просьбе его повторно отправляет парк, {operator} или {provider}, и сумма снова списывается с вашего баланса в Yandex, как при новом выводе.',
          'Проверка. Если неясно, списана ли сумма с баланса в Yandex или выполнен ли перевод, либо сумму не удалось автоматически вернуть на баланс в Yandex, вывод проверяется вручную, прежде чем что-либо будет повторено или возвращено, — чтобы вам не заплатили дважды и деньги не потерялись. Затем вывод либо выполняется, либо его сумма возвращается на ваш баланс в Yandex.',
          'Ошибочное зачисление. Если деньги поступили вам по ошибке — например, один и тот же вывод дважды или вывод, сумма которого также вернулась на ваш баланс в Yandex, — вы обязаны их вернуть. Парк может потребовать возврата или уменьшить на эту сумму ваш баланс в Yandex.',
        ),
      ],
    },
    {
      id: 'fee',
      title: 'Комиссия',
      blocks: [
        ul(
          'С каждого выполненного вывода удерживается фиксированная комиссия, сейчас 0,50 лари, которая остаётся на счёте парка (долю PayTaxi парк и PayTaxi рассчитывают между собой отдельно): при выводе 100 лари на счёт поступит 99,50 лари.',
          'Отменённый или невыполненный вывод бесплатен; других платежей за пользование PayTaxi нет.',
          'Комиссия изменяется только в порядке, описанном в разделе «Изменение условий». К выводу применяется комиссия, действующая в момент его подтверждения; у уже начатого вывода комиссия не меняется.',
        ),
      ],
    },
    {
      id: 'liability',
      title: 'Ответственность',
      blocks: [
        p('Мы отвечаем за то, чтобы PayTaxi работал так, как описано в этих условиях. Если из-за ошибки платформы сумма была неверно списана или зачислена, мы разберёмся и исправим это — вместе с парком, если дело касается его счёта.'),
        p('Помимо исправления неверного списания или зачисления, наша ответственность перед вами ограничена прямым доказанным ущербом, без упущенной выгоды. Каждая компания отвечает только за свою часть: {provider} — за правильную работу платформы, {operator} — за поддержку и отношения с вашим парком.'),
        p('PayTaxi не отвечает:'),
        ul(
          'за вашу работу водителем — поездки, пассажиров, автомобиль, соблюдение правил дорожного движения и ваши налоги;',
          'за то, как Yandex и ваш парк рассчитывают ваш заработок и баланс;',
          'за то, держит ли парк на банковском счёте достаточно средств;',
          'за сбои банков, Yandex, мобильной сети или интернета и кратковременные технические работы: вывод, задержанный банком или из-за нехватки средств, ставится в очередь и выполняется автоматически, а пока Yandex недоступен, новый вывод сделать нельзя;',
          'за события вне нашего разумного контроля (форс-мажор): стихийные бедствия, военные действия, масштабные отключения электроэнергии, кибератаки или изменения законодательства;',
          'за ущерб, вызванный тем, что вы сообщили кому-то код или указали ложные данные.',
        ),
        p('Эти условия не ограничивают ответственность, ограничение которой не допускает законодательство Грузии, например за ущерб, причинённый умышленно или по грубой неосторожности.'),
      ],
    },
    {
      id: 'termination',
      title: 'Приостановка и прекращение',
      blocks: [
        ul(
          'Вы можете перестать пользоваться PayTaxi в любое время; чтобы приостановить аккаунт PayTaxi, обратитесь в парк или в поддержку PayTaxi.',
          'Парк приостанавливает ваш аккаунт PayTaxi, если вы перестали с ним сотрудничать, по вашей просьбе или при потере телефона (см. «Вход и безопасность аккаунта»). Парк вправе приостановить его и за ложные данные, мошенничество или попытку мошенничества, выдачу себя за другое лицо, использование чужого аккаунта или счёта либо злоупотребление, которое угрожает другим пользователям или платформе; обнаружив такое, {operator} или {provider} вправе сделать это от имени парка. С приостановленным аккаунтом нельзя войти в приложение и вывести деньги — приложение сообщит об этом; вы можете запросить объяснение причины, а возобновить аккаунт может парк.',
          'Использование PayTaxi вашим парком может быть приостановлено по его договору о пользовании PayTaxi: новые выводы тогда приостанавливаются, а уже ожидающие продолжаются в порядке, описанном выше.',
          'Приостановка аккаунта не останавливает уже начатый вывод: он будет завершён, а если его отменят или он не выполнится, сумма вернётся на ваш баланс в Yandex.',
          'Если ваш парк перестанет пользоваться PayTaxi, мы по возможности заранее сообщим об этом в приложении. Новые выводы тогда прекращаются, начатые выводы либо завершаются, либо отменяются, а ваши данные обрабатываются, как описано в Политике конфиденциальности.',
          'Если лицензионный договор оператора прекратится, функции оператора, включая его права и обязанности по этим условиям, переходят к владельцу платформы или к названной им компании; мы сообщим об этом в приложении, и эти условия продолжат действовать.',
        ),
      ],
    },
    {
      id: 'privacy',
      title: 'Персональные данные и сообщения',
      blocks: [
        p('Лицо, ответственное за обработку ваших персональных данных в PayTaxi, — ваш парк. {provider} обрабатывает их от имени парка как лицо, уполномоченное на обработку, и только для услуги вывода средств; {operator} получает к ним доступ от имени парка для подключения водителей и поддержки. Исключение — история ваших входов, которая подтверждает, что вы приняли эти условия: её {provider} и {operator} хранят от своего имени. Подробности и ваши права описаны в Политике конфиденциальности PayTaxi — ссылка на неё есть на странице входа.'),
        p('По SMS мы присылаем только коды для входа; сообщения о выводах приходят в приложение. Рекламных и маркетинговых сообщений мы не отправляем.'),
      ],
    },
    {
      id: 'changes',
      title: 'Изменение условий',
      blocks: [
        p('Мы изменяем эти условия только при изменении законодательства, правил банков или Yandex, требований безопасности или функций PayTaxi, а также чтобы изменить комиссию. Мы сообщим об изменении в приложении не менее чем за 7 календарных дней до его вступления в силу. Если вы не согласны, вы можете до этого прекратить пользоваться PayTaxi. Вывод, подтверждённый после вступления изменения в силу, выполняется на новых условиях, а подтверждённый раньше — на условиях, действовавших при его подтверждении.'),
      ],
    },
    {
      id: 'contact',
      title: 'Контакты, споры и применимое право',
      blocks: [
        p('С вопросами о выводе, балансе или аккаунте обращайтесь в свой парк или в поддержку PayTaxi: {support}. Технические вопросы поддержка передаёт владельцу платформы. Запросы о персональных данных: ваш парк или {email}. Почтовый адрес: {provider}, {providerAddress}.'),
        p('Любые претензии и разногласия мы постараемся урегулировать путём переговоров; если это не удастся, спор рассматривает компетентный суд Грузии. Эти условия регулируются законодательством Грузии. При расхождении между грузинской, английской и русской версиями преимущество имеет грузинский текст.'),
      ],
    },
  ],
  footer: 'Платформа PayTaxi: {provider}. Оператор в Грузии: {operator}.',
};

export const TERMS_DOCS: Record<LegalLang, TermsDoc> = { ka, en, ru };
